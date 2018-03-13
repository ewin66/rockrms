﻿// <copyright>
// Copyright by the Spark Development Network
//
// Licensed under the Rock Community License (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
// http://www.rockrms.com/license
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// </copyright>
//
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Web;

using Quartz;

using Rock.Data;
using Rock.Model;
using Rock.SystemKey;
using Rock.Web.Cache;

namespace Rock.Jobs
{
    /// <summary>
    /// Job to update people/families based on the Data Automation settings.
    /// </summary>
    [DisallowConcurrentExecution]
    public class DataAutomation : IJob
    {
        #region Constructor

        /// <summary> 
        /// Empty constructor for job initilization
        /// <para>
        /// Jobs require a public empty constructor so that the
        /// scheduler can instantiate the class whenever it needs.
        /// </para>
        /// </summary>
        public DataAutomation()
        {
        }

        #endregion Constructor

        /// <summary>
        /// Executes the specified context.
        /// </summary>
        /// <param name="context">The context.</param>
        /// <exception cref="System.NotImplementedException"></exception>
        public void Execute( IJobExecutionContext context )
        {
            string reactivateResult = ReactivatePeople( context );
            string inactivateResult = InactivatePeople( context );
            string updateFamilyCampusResult = UpdateFamilyCampus( context );
            string moveAdultChildrenResult = MoveAdultChildren( context );

            context.UpdateLastStatusMessage( $@"Reactivate People: {reactivateResult},
Inactivate People: {inactivateResult}
Update Family Campus: {updateFamilyCampusResult}
Move Adult Children: {moveAdultChildrenResult}
" );
        }

        #region Reactivate People

        private string ReactivatePeople( IJobExecutionContext context )
        {
            try
            {
                var settings = Rock.Web.SystemSettings.GetValue( SystemSetting.DATA_AUTOMATION_REACTIVATE_PEOPLE ).FromJsonOrNull<Utility.Settings.DataAutomation.ReactivatePeople>();
                if ( settings == null || !settings.IsEnabled )
                {
                    return "Not Enabled";
                }

                // Get the family group type
                var familyGroupType = GroupTypeCache.Read( SystemGuid.GroupType.GROUPTYPE_FAMILY.AsGuid() );
                if ( familyGroupType == null )
                {
                    throw new Exception( "Could not determine the 'Family' group type." );
                }

                // Get the active record status defined value
                var activeStatus = DefinedValueCache.Read( Rock.SystemGuid.DefinedValue.PERSON_RECORD_STATUS_ACTIVE.AsGuid() );
                if ( activeStatus == null )
                {
                    throw new Exception( "Could not determine the 'Active' record status value." );
                }

                // Get the inactive record status defined value
                var inactiveStatus = DefinedValueCache.Read( Rock.SystemGuid.DefinedValue.PERSON_RECORD_STATUS_INACTIVE.AsGuid() );
                if ( inactiveStatus == null )
                {
                    throw new Exception( "Could not determine the 'Inactive' record status value." );
                }

                var personIds = new List<int>();

                using ( var rockContext = new RockContext() )
                {
                    // Get all the person ids with selected activity
                    personIds = GetPeopleWhoContributed( settings.IsLastContributionEnabled, settings.LastContributionPeriod, rockContext );
                    personIds.AddRange( GetPeopleWhoAttendedServiceGroup( settings.IsAttendanceInServiceGroupEnabled, settings.AttendanceInServiceGroupPeriod, rockContext ) );
                    personIds.AddRange( GetPeopleWhoAttendedGroupType( settings.IsAttendanceInGroupTypeEnabled, settings.AttendanceInGroupType, settings.AttendanceInGroupTypeDays, rockContext ) );
                    personIds.AddRange( GetPeopleWhoSubmittedPrayerRequest( settings.IsPrayerRequestEnabled, settings.PrayerRequestPeriod, rockContext ) );
                    personIds.AddRange( GetPeopleWithPersonAttributUpdates( settings.IsPersonAttributesEnabled, settings.PersonAttributes, settings.PersonAttributesDays, rockContext ) );
                    personIds.AddRange( GetPeopleWithInteractions( settings.IsInteractionsEnabled, settings.Interactions, rockContext ) );
                    personIds.AddRange( GetPeopleInDataView( settings.IsIncludeDataViewEnabled, settings.IncludeDataView, rockContext ) );
                    personIds = personIds.Distinct().ToList();

                    // Expand the list to all family members.
                    personIds = new GroupMemberService( rockContext )
                        .Queryable().AsNoTracking()
                        .Where( m =>
                            m.Group != null &&
                            m.Group.GroupTypeId == familyGroupType.Id &&
                            personIds.Contains( m.PersonId ) )
                        .SelectMany( m => m.Group.Members )
                        .Select( p => p.PersonId )
                        .ToList();
                    personIds = personIds.Distinct().ToList();

                    // Start the person qry by getting any of the people who are currently inactive
                    var personQry = new PersonService( rockContext )
                        .Queryable().AsNoTracking()
                        .Where( p =>
                            personIds.Contains( p.Id ) &&
                            p.RecordStatusValueId == inactiveStatus.Id );

                    // Check to see if any inactive reasons should be ignored, and if so filter the list to exclude those
                    var invalidReasonDt = DefinedTypeCache.Read( SystemGuid.DefinedType.PERSON_RECORD_STATUS_REASON.AsGuid() );
                    if ( invalidReasonDt != null )
                    {
                        var invalidReasonIds = invalidReasonDt.DefinedValues
                            .Where( a =>
                                a.AttributeValues.ContainsKey( "AllowAutomatedReactivation" ) &&
                                !a.AttributeValues["AllowAutomatedReactivation"].Value.AsBoolean() )
                            .Select( a => a.Id )
                            .ToList();
                        if ( invalidReasonIds.Any() )
                        {
                            personQry = personQry.Where( p =>
                                !p.RecordStatusReasonValueId.HasValue ||
                                !invalidReasonIds.Contains( p.RecordStatusReasonValueId.Value ) );
                        }
                    }

                    // If any people should be excluded based on being part of a dataview, exclude those people
                    var excludePersonIds = GetPeopleInDataView( settings.IsExcludeDataViewEnabled, settings.ExcludeDataView, rockContext );
                    if ( excludePersonIds.Any() )
                    {
                        personQry = personQry.Where( p =>
                            !excludePersonIds.Contains( p.Id ) );
                    }


                    // Run the query
                    personIds = personQry.Select( p => p.Id ).ToList();
                }

                // Counter for displaying results
                int recordsProcessed = 0;
                int recordsUpdated = 0;
                int totalRecords = personIds.Count();

                // Loop through each person
                foreach ( var personId in personIds )
                {
                    // Update the status on every 100th record
                    recordsProcessed++;
                    if ( recordsProcessed % 100 == 0 )
                    {
                        context.UpdateLastStatusMessage( $"Processing person reactivate: Activated {recordsProcessed:N0} of {totalRecords:N0} person records." );
                    }

                    // Reactivate the person
                    using ( var rockContext = new RockContext() )
                    {
                        var person = new PersonService( rockContext ).Get( personId );
                        if ( person != null )
                        {
                            person.RecordStatusValueId = activeStatus.Id;
                            person.RecordStatusReasonValueId = null;
                            rockContext.SaveChanges();

                            recordsUpdated++;
                        }
                    }
                }

                // Format the result message
                return $"{recordsProcessed:N0} people were processed; {recordsUpdated:N0} were activated.";

            }
            catch ( Exception ex )
            {
                // Log exception and return the exception messages.
                HttpContext context2 = HttpContext.Current;
                ExceptionLogService.LogException( ex, context2 );

                return ex.Messages().AsDelimited( "; " );
            }
        }

        #endregion

        #region Inactivate People

        private string InactivatePeople( IJobExecutionContext context )
        {
            try
            {
                var settings = Rock.Web.SystemSettings.GetValue( SystemSetting.DATA_AUTOMATION_INACTIVATE_PEOPLE ).FromJsonOrNull<Utility.Settings.DataAutomation.InactivatePeople>();
                if ( settings == null || !settings.IsEnabled )
                {
                    return "Not Enabled";
                }

                // Get the family group type
                var familyGroupType = GroupTypeCache.Read( SystemGuid.GroupType.GROUPTYPE_FAMILY.AsGuid() );
                if ( familyGroupType == null )
                {
                    throw new Exception( "Could not determine the 'Family' group type." );
                }

                // Get the active record status defined value
                var activeStatus = DefinedValueCache.Read( Rock.SystemGuid.DefinedValue.PERSON_RECORD_STATUS_ACTIVE.AsGuid() );
                if ( activeStatus == null )
                {
                    throw new Exception( "Could not determine the 'Active' record status value." );
                }

                // Get the inactive record status defined value
                var inactiveStatus = DefinedValueCache.Read( Rock.SystemGuid.DefinedValue.PERSON_RECORD_STATUS_INACTIVE.AsGuid() );
                if ( inactiveStatus == null )
                {
                    throw new Exception( "Could not determine the 'Inactive' record status value." );
                }

                // Get the inactive record status defined value
                var inactiveReason = DefinedValueCache.Read( Rock.SystemGuid.DefinedValue.PERSON_RECORD_STATUS_REASON_NO_ACTIVITY.AsGuid() );
                if ( inactiveReason == null )
                {
                    throw new Exception( "Could not determine the 'No Activity' record status reason value." );
                }
                var personIds = new List<int>();
                using ( var rockContext = new RockContext() )
                {
                    // Get all the person ids with selected activity
                    personIds = GetPeopleWhoContributed( settings.IsNoLastContributionEnabled, settings.NoLastContributionPeriod, rockContext );
                    personIds.AddRange( GetPeopleWhoAttendedServiceGroup( settings.IsNoAttendanceInServiceGroupEnabled, settings.NoAttendanceInServiceGroupPeriod, rockContext ) );
                    personIds.AddRange( GetPeopleWhoAttendedGroupType( settings.IsNoAttendanceInGroupTypeEnabled, settings.AttendanceInGroupType, settings.NoAttendanceInGroupTypeDays, rockContext ) );
                    personIds.AddRange( GetPeopleWhoSubmittedPrayerRequest( settings.IsNoPrayerRequestEnabled, settings.NoPrayerRequestPeriod, rockContext ) );
                    personIds.AddRange( GetPeopleWithPersonAttributUpdates( settings.IsNoPersonAttributesEnabled, settings.PersonAttributes, settings.NoPersonAttributesDays, rockContext ) );
                    personIds.AddRange( GetPeopleWithInteractions( settings.IsNoInteractionsEnabled, settings.NoInteractions, rockContext ) );
                    personIds = personIds.Distinct().ToList();

                    // Expand the list to all family members.
                    personIds = new GroupMemberService( rockContext )
                        .Queryable().AsNoTracking()
                        .Where( m =>
                            m.Group != null &&
                            m.Group.GroupTypeId == familyGroupType.Id &&
                            personIds.Contains( m.PersonId ) )
                        .SelectMany( m => m.Group.Members )
                        .Select( p => p.PersonId )
                        .ToList();
                    personIds = personIds.Distinct().ToList();

                    // Start the person qry by getting any of the people who are currently active and not in the list of people with activity
                    var personQry = new PersonService( rockContext )
                        .Queryable().AsNoTracking()
                        .Where( p =>
                            !personIds.Contains( p.Id ) &&
                            p.RecordStatusValueId == activeStatus.Id );

                    // If any people should be excluded based on being part of a dataview, exclude those people
                    var excludePersonIds = GetPeopleInDataView( settings.IsNotInDataviewEnabled, settings.NotInDataview, rockContext );
                    if ( excludePersonIds.Any() )
                    {
                        personQry = personQry.Where( p =>
                            !excludePersonIds.Contains( p.Id ) );
                    }

                    // Run the query
                    personIds = personQry.Select( p => p.Id ).ToList();
                }

                // Counter for displaying results
                int recordsProcessed = 0;
                int recordsUpdated = 0;
                int totalRecords = personIds.Count();

                // Loop through each person
                foreach ( var personId in personIds )
                {
                    // Update the status on every 100th record
                    recordsProcessed++;
                    if ( recordsProcessed % 100 == 0 )
                    {
                        context.UpdateLastStatusMessage( $"Processing person inactivate: Inactivated {recordsProcessed:N0} of {totalRecords:N0} person records." );
                    }

                    // Inactivate the person
                    using ( var rockContext = new RockContext() )
                    {
                        var person = new PersonService( rockContext ).Get( personId );
                        if ( person != null )
                        {
                            person.RecordStatusValueId = inactiveStatus.Id;
                            person.RecordStatusReasonValueId = inactiveReason.Id;
                            person.InactiveReasonNote = "Inactivated by the Data Automation Job";
                            rockContext.SaveChanges();

                            recordsUpdated++;
                        }
                    }
                }

                // Format the result message
                return $"{recordsProcessed:N0} people were processed; {recordsUpdated:N0} were inactivated.";

            }
            catch ( Exception ex )
            {
                // Log exception and return the exception messages.
                HttpContext context2 = HttpContext.Current;
                ExceptionLogService.LogException( ex, context2 );

                return ex.Messages().AsDelimited( "; " );
            }
        }

        #endregion

        #region Update Family Campus 

        private string UpdateFamilyCampus( IJobExecutionContext context )
        {
            try
            {
                var settings = Rock.Web.SystemSettings.GetValue( SystemSetting.DATA_AUTOMATION_CAMPUS_UPDATE ).FromJsonOrNull<Utility.Settings.DataAutomation.UpdateFamilyCampus>();
                if ( settings == null || !settings.IsEnabled )
                {
                    return "Not Enabled";
                }

                // Get the family group type and roles
                var familyGroupType = GroupTypeCache.Read( SystemGuid.GroupType.GROUPTYPE_FAMILY.AsGuid() );
                if ( familyGroupType == null )
                {
                    throw new Exception( "Could not determine the 'Family' group type." );
                }

                var familyIds = new List<int>();

                using ( RockContext rockContext = new RockContext() )
                {
                    // Start a qry for all family ids
                    var familyIdQry = new GroupService( rockContext )
                        .Queryable().AsNoTracking()
                        .Where( g => g.GroupTypeId == familyGroupType.Id );

                    // Check to see if we should ignore any families that had a manual update
                    if ( settings.IsIgnoreIfManualUpdateEnabled )
                    {
                        // Figure out how far back to look
                        var startPeriod = RockDateTime.Now.AddDays( -settings.IgnoreIfManualUpdatePeriod );

                        // Find any families that has a campus manually added/updated within the configured number of days
                        var personEntityTypeId = EntityTypeCache.Read( typeof( Person ) ).Id;
                        var familyIdsWithManualUpdate = new HistoryService( rockContext )
                            .Queryable().AsNoTracking()
                            .Where( m =>
                                m.EntityTypeId == personEntityTypeId &&
                                m.CreatedDateTime >= startPeriod && m.RelatedEntityId.HasValue &&
                                m.Summary.Contains( "<span class='field name'>Campus</span>" ) )
                            .Select( a => a.RelatedEntityId.Value )
                            .ToList()
                            .Distinct();

                        familyIdQry = familyIdQry.Where( f => familyIdsWithManualUpdate.Contains( f.Id ) );
                    }

                    // Query for the family ids
                    familyIds = familyIdQry.Select( f => f.Id ).ToList();
                }

                // Counters for displaying results
                int recordsProcessed = 0;
                int recordsUpdated = 0;
                int totalRecords = familyIds.Count();

                // Loop through each family
                foreach ( var familyId in familyIds )
                {
                    // Update the status on every 100th record
                    recordsProcessed++;
                    if ( recordsProcessed % 100 == 0 )
                    {
                        context.UpdateLastStatusMessage( $"Processing campus updates: {recordsProcessed:N0} of {totalRecords:N0} families processed; campus has been updated for {recordsUpdated:N0} of them." );
                    }

                    // Using a new rockcontext for each one (to improve performance)
                    using ( var rockContext = new RockContext() )
                    {
                        // Get the family
                        var groupService = new GroupService( rockContext );
                        var family = groupService.Get( familyId );

                        var personIds = family.Members.Select( m => m.PersonId ).ToList();

                        // Calculate the campus based on family attendance
                        int? attendanceCampusId = null;
                        int attendanceCampusCount = 0;
                        if ( settings.IsMostFamilyAttendanceEnabled )
                        {
                            var startPeriod = RockDateTime.Now.AddDays( -settings.MostFamilyAttendancePeriod );
                            var attendanceCampus = new AttendanceService( rockContext )
                                .Queryable().AsNoTracking()
                                .Where( a =>
                                    a.StartDateTime >= startPeriod &&
                                    a.CampusId.HasValue &&
                                    a.DidAttend == true &&
                                    personIds.Contains( a.PersonAlias.PersonId ) )
                                .GroupBy( a => a.CampusId )
                                .OrderByDescending( a => a.Count() )
                                .Select( a => new
                                {
                                    CampusId = a.Key,
                                    Count = a.Count()
                                } )
                                .FirstOrDefault();
                            if ( attendanceCampus != null )
                            {
                                attendanceCampusId = attendanceCampus.CampusId;
                                attendanceCampusCount = attendanceCampus.Count;
                            }
                        }

                        // Calculate the campus based on giving
                        int? givingCampusId = null;
                        int givingCampusCount = 0;
                        if ( settings.IsMostFamilyGivingEnabled )
                        {
                            var startPeriod = RockDateTime.Now.AddDays( -settings.MostFamilyAttendancePeriod );
                            var givingCampus = new FinancialTransactionDetailService( rockContext )
                                .Queryable().AsNoTracking()
                                .Where( a =>
                                    a.Transaction != null &&
                                    a.Transaction.TransactionDateTime.HasValue &&
                                    a.Transaction.TransactionDateTime >= startPeriod &&
                                    a.Transaction.AuthorizedPersonAliasId.HasValue &&
                                    personIds.Contains( a.Transaction.AuthorizedPersonAlias.PersonId ) &&
                                    a.Account.CampusId.HasValue )
                                .GroupBy( a => a.Account.CampusId )
                                .OrderByDescending( a => a.Select( b => b.Transaction ).Distinct().Count() )
                                .Select( a => new
                                {
                                    CampusId = a.Key,
                                    Count = a.Count()
                                } )
                                .FirstOrDefault();
                            if ( givingCampus != null )
                            {
                                givingCampusId = givingCampus.CampusId;
                                givingCampusCount = givingCampus.Count;
                            }
                        }

                        // If a campus could not be calculated for attendance or giving, move to next family.
                        if ( !attendanceCampusId.HasValue && !givingCampusId.HasValue )
                        {
                            continue;
                        }

                        // Figure out what the campus should be
                        int? currentCampusId = family.CampusId;
                        int? newCampusId = null;
                        if ( attendanceCampusId.HasValue )
                        {
                            if ( givingCampusId.HasValue && givingCampusId.Value != attendanceCampusId.Value )
                            {
                                // If campus from attendance and giving are different
                                switch ( settings.MostAttendanceOrGiving )
                                {
                                    case Utility.Settings.DataAutomation.CampusCriteria.UseGiving:
                                        newCampusId = givingCampusId;
                                        break;

                                    case Utility.Settings.DataAutomation.CampusCriteria.UseAttendance:
                                        newCampusId = attendanceCampusId;
                                        break;

                                    case Utility.Settings.DataAutomation.CampusCriteria.UseHighestFrequency:

                                        // If frequency is the same for both, and either of the values are same as current, then don't change the campus
                                        if ( attendanceCampusCount == givingCampusCount &&
                                            currentCampusId.HasValue &&
                                            ( currentCampusId.Value == attendanceCampusId.Value || currentCampusId.Value == givingCampusId.Value ) )
                                        {
                                            newCampusId = null;
                                        }
                                        else
                                        {
                                            newCampusId = ( attendanceCampusCount > givingCampusCount ) ? attendanceCampusId : givingCampusId;
                                        }

                                        break;

                                        // if none of those, just ignore.
                                }
                            }
                            else
                            {
                                newCampusId = attendanceCampusId;
                            }
                        }
                        else
                        {
                            newCampusId = givingCampusId;
                        }

                        // Campus did not change
                        if ( !newCampusId.HasValue || newCampusId.Value == ( currentCampusId ?? 0 ) )
                        {
                            continue;
                        }

                        // Check to see if the campus change should be ignored
                        if ( currentCampusId.HasValue )
                        {
                            bool ignore = false;
                            foreach ( var exclusion in settings.IgnoreCampusChanges )
                            {
                                if ( exclusion.FromCampus == currentCampusId.Value && exclusion.ToCampus == newCampusId )
                                {
                                    ignore = true;
                                    break;
                                }
                            }

                            if ( ignore )
                            {
                                continue;
                            }
                        }

                        // Update the campus
                        family.CampusId = newCampusId.Value;
                        rockContext.SaveChanges();

                        // Since we just succesfully saved the change, increment the update counter
                        recordsUpdated++;
                    }
                }

                // Format the result message
                return $"{recordsProcessed:N0} families were processed; campus was updated for {recordsUpdated:N0} of them.";

            }
            catch ( Exception ex )
            {
                // Log exception and return the exception messages.
                HttpContext context2 = HttpContext.Current;
                ExceptionLogService.LogException( ex, context2 );

                return ex.Messages().AsDelimited( "; " );
            }
        }

        #endregion

        #region Move Adult Children 

        private string MoveAdultChildren( IJobExecutionContext context )
        {
            try
            {
                var settings = Rock.Web.SystemSettings.GetValue( SystemSetting.DATA_AUTOMATION_ADULT_CHILDREN ).FromJsonOrNull<Utility.Settings.DataAutomation.MoveAdultChildren>();
                if ( settings == null || !settings.IsEnabled )
                {
                    return "Not Enabled";
                }

                // Get some system guids
                var activeRecordStatusGuid = SystemGuid.DefinedValue.PERSON_RECORD_STATUS_ACTIVE.AsGuid();
                var homeAddressGuid = SystemGuid.DefinedValue.GROUP_LOCATION_TYPE_HOME.AsGuid();
                var homePhoneGuid = SystemGuid.DefinedValue.PERSON_PHONE_TYPE_HOME.AsGuid();
                var personChangesGuid = SystemGuid.Category.HISTORY_PERSON_DEMOGRAPHIC_CHANGES.AsGuid();
                var familyChangesGuid = SystemGuid.Category.HISTORY_PERSON_FAMILY_CHANGES.AsGuid();

                // Get the family group type and roles
                var familyGroupType = GroupTypeCache.Read( SystemGuid.GroupType.GROUPTYPE_FAMILY.AsGuid() );
                if ( familyGroupType == null )
                {
                    throw new Exception( "Could not determine the 'Family' group type." );
                }
                var childRole = familyGroupType.Roles.FirstOrDefault( r => r.Guid == SystemGuid.GroupRole.GROUPROLE_FAMILY_MEMBER_CHILD.AsGuid() );
                var adultRole = familyGroupType.Roles.FirstOrDefault( r => r.Guid == SystemGuid.GroupRole.GROUPROLE_FAMILY_MEMBER_ADULT.AsGuid() );
                if ( childRole == null || adultRole == null )
                {
                    throw new Exception( "Could not determine the 'Adult' and 'Child' roles." );
                }

                // Calculate the date to use for determining if someone is an adult based on their age (birthdate)
                var adultBirthdate = RockDateTime.Today.AddYears( 0 - settings.AdultAge );

                // Get a list of people marked as a child in any family, but who are now an "adult" based on their age
                var adultChildIds = new List<int>();
                using ( var rockContext = new RockContext() )
                {
                    adultChildIds = new GroupMemberService( rockContext )
                        .Queryable().AsNoTracking()
                        .Where( m =>
                            m.GroupRoleId == childRole.Id &&
                            m.Person.BirthDate.HasValue &&
                            m.Person.BirthDate <= adultBirthdate &&
                            m.Person.RecordStatusValue != null &&
                            m.Person.RecordStatusValue.Guid == activeRecordStatusGuid )
                        .OrderBy( m => m.PersonId )
                        .Select( m => m.PersonId )
                        .Distinct()
                        .Take( settings.MaximumRecords )
                        .ToList();
                }

                // Counter for displaying results
                int recordsProcessed = 0;
                int recordsUpdated = 0;
                int totalRecords = adultChildIds.Count();

                // Loop through each person
                foreach ( int personId in adultChildIds )
                {
                    // Update the status on every 100th record
                    recordsProcessed++;
                    if ( recordsProcessed % 100 == 0 )
                    {
                        context.UpdateLastStatusMessage( $"Processing Adult Children: {recordsProcessed:N0} of {totalRecords:N0} children processed; {recordsUpdated:N0} have been moved to their own family." );
                    }

                    // Using a new rockcontext for each one (to improve performance)
                    using ( var rockContext = new RockContext() )
                    {
                        // Get all the 'family' group member records for this person.
                        var groupMemberService = new GroupMemberService( rockContext );
                        var groupMembers = groupMemberService.Queryable()
                            .Where( m =>
                                m.PersonId == personId &&
                                m.Group.GroupTypeId == familyGroupType.Id )
                            .ToList();

                        // If there are no group members (shouldn't happen), just ignore and keep going
                        if ( !groupMembers.Any() )
                        {
                            continue;
                        }

                        // Get a reference to the person
                        var person = groupMembers.First().Person;

                        // Get the person's primary family, and if we can't get that (something else that shouldn't happen), just ignore this person.
                        var primaryFamily = person.GetFamilies( rockContext ).FirstOrDefault();
                        //var primaryFamily = person.GetP.PrimaryFamily;  TODO: Use this instead in V8
                        if ( primaryFamily == null )
                        {
                            continue;
                        }

                        // Setup a variable for tracking person changes
                        var personChanges = new List<string>();

                        // Get all the parent and sibling ids (for adding relationships later)
                        var parentIds = groupMembers
                            .SelectMany( m => m.Group.Members )
                            .Where( m =>
                                m.PersonId != personId &&
                                m.GroupRoleId == adultRole.Id )
                            .Select( m => m.PersonId )
                            .Distinct()
                            .ToList();

                        var siblingIds = groupMembers
                            .SelectMany( m => m.Group.Members )
                            .Where( m =>
                                m.PersonId != personId &&
                                m.GroupRoleId == childRole.Id )
                            .Select( m => m.PersonId )
                            .Distinct()
                            .ToList();

                        // If person is already an adult in any family, lets find the first one, and use that as the new family
                        var newFamily = groupMembers
                            .Where( m => m.GroupRoleId == adultRole.Id )
                            .OrderBy( m => m.GroupOrder )
                            .Select( m => m.Group )
                            .FirstOrDefault();

                        // If person was not already an adult in any family, let's look for a family where they are the only person, or create a new one
                        if ( newFamily == null )
                        {
                            // Try to find a family where they are the only one in the family.
                            newFamily = groupMembers
                                .Select( m => m.Group )
                                .Where( g => !g.Members.Any( m => m.PersonId != personId ) )
                                .FirstOrDefault();

                            // If we found one, make them an adult in that family
                            if ( newFamily != null )
                            {
                                // The current person should be the only one in this family, but lets loop through each member anyway
                                foreach ( var groupMember in groupMembers.Where( m => m.GroupId == newFamily.Id ) )
                                {
                                    groupMember.GroupRoleId = adultRole.Id;
                                }

                                // Save role change to history
                                var memberChanges = new List<string>();
                                History.EvaluateChange( memberChanges, "Role", string.Empty, adultRole.Name );
                                HistoryService.SaveChanges( rockContext, typeof( Person ), familyChangesGuid, personId, memberChanges, newFamily.Name, typeof( Group ), newFamily.Id, false );
                            }
                            else
                            {
                                // If they are not already an adult in a family, and they're not in any family by themeselves, we need to create a new family for them.
                                // The SaveNewFamily adds history records for this

                                // Create a new group member and family
                                var groupMember = new GroupMember
                                {
                                    Person = person,
                                    GroupRoleId = adultRole.Id,
                                    GroupMemberStatus = GroupMemberStatus.Active
                                };
                                newFamily = GroupService.SaveNewFamily( rockContext, new List<GroupMember> { groupMember }, primaryFamily.CampusId, false );
                            }
                        }

                        // If user configured the job to copy home address and this person's family does not have any home addresses, copy them from the primary family
                        if ( settings.UseSameHomeAddress && !newFamily.GroupLocations.Any( l => l.GroupLocationTypeValue != null && l.GroupLocationTypeValue.Guid == homeAddressGuid ) )
                        {
                            var familyChanges = new List<string>();

                            foreach ( var groupLocation in primaryFamily.GroupLocations.Where( l => l.GroupLocationTypeValue != null && l.GroupLocationTypeValue.Guid == homeAddressGuid ) )
                            {
                                newFamily.GroupLocations.Add( new GroupLocation
                                {
                                    LocationId = groupLocation.LocationId,
                                    GroupLocationTypeValueId = groupLocation.GroupLocationTypeValueId,
                                    IsMailingLocation = groupLocation.IsMailingLocation,
                                    IsMappedLocation = groupLocation.IsMappedLocation
                                } );

                                History.EvaluateChange( familyChanges, groupLocation.GroupLocationTypeValue.Value + " Location", string.Empty, groupLocation.Location.ToString() );
                            }

                            HistoryService.SaveChanges( rockContext, typeof( Person ), familyChangesGuid, personId, familyChanges, false );
                        }

                        // If user configured the job to copy home phone and this person does not have a home phone, copy the first home phone number from another adult in original family(s)
                        if ( settings.UseSameHomePhone && !person.PhoneNumbers.Any( p => p.NumberTypeValue != null && p.NumberTypeValue.Guid == homePhoneGuid ) )
                        {

                            // First look for adults in primary family
                            var homePhone = primaryFamily.Members
                                .Where( m =>
                                    m.PersonId != person.Id &&
                                    m.GroupRoleId == adultRole.Id )
                                .SelectMany( m => m.Person.PhoneNumbers )
                                .FirstOrDefault( p => p.NumberTypeValue != null && p.NumberTypeValue.Guid == homePhoneGuid );

                            // If one was not found in primary family, look in any of the person's other families
                            if ( homePhone == null )
                            {
                                homePhone = groupMembers
                                    .Where( m => m.GroupId != primaryFamily.Id )
                                    .SelectMany( m => m.Group.Members )
                                    .Where( m =>
                                        m.PersonId != person.Id &&
                                        m.GroupRoleId == adultRole.Id )
                                    .SelectMany( m => m.Person.PhoneNumbers )
                                    .FirstOrDefault( p => p.NumberTypeValue != null && p.NumberTypeValue.Guid == homePhoneGuid );
                            }

                            // If we found one, add it to the person
                            if ( homePhone != null )
                            {
                                person.PhoneNumbers.Add( new PhoneNumber
                                {
                                    CountryCode = homePhone.CountryCode,
                                    Number = homePhone.Number,
                                    NumberFormatted = homePhone.NumberFormatted,
                                    NumberReversed = homePhone.NumberReversed,
                                    Extension = homePhone.Extension,
                                    NumberTypeValueId = homePhone.NumberTypeValueId,
                                    IsMessagingEnabled = homePhone.IsMessagingEnabled,
                                    IsUnlisted = homePhone.IsUnlisted,
                                    Description = homePhone.Description
                                } );
                            }
                        }

                        // At this point, the person was either already an adult in one or more families, 
                        //   or we updated one of their records to be an adult, 
                        //   or we created a new family with them as an adult. 
                        // So now we should delete any of the remaining family member records where they are still a child.
                        foreach ( var groupMember in groupMembers.Where( m => m.GroupRoleId == childRole.Id ) )
                        {
                            groupMemberService.Delete( groupMember );
                        }

                        // Save all the changes
                        rockContext.SaveChanges();

                        // Since we just succesfully saved the change, increment the update counter
                        recordsUpdated++;

                        // If configured to do so, add any parent relationships (these methods take care of logging changes)
                        if ( settings.ParentRelationshipId.HasValue )
                        {
                            foreach ( int parentId in parentIds )
                            {
                                groupMemberService.CreateKnownRelationship( personId, parentId, settings.ParentRelationshipId.Value );
                            }
                        }

                        // If configured to do so, add any sibling relationships
                        if ( settings.SiblingRelationshipId.HasValue )
                        {
                            foreach ( int siblingId in siblingIds )
                            {
                                groupMemberService.CreateKnownRelationship( personId, siblingId, settings.SiblingRelationshipId.Value );
                            }
                        }

                        // Look for any workflows
                        if ( settings.WorkflowTypeIds.Any() )
                        {
                            // Create parameters for the old/new family
                            var workflowParameters = new Dictionary<string, string>
                            {
                                { "OldFamily", primaryFamily.Guid.ToString() },
                                { "NewFamily", newFamily.Guid.ToString() }
                            };

                            // Launch all the workflows
                            foreach ( var wfId in settings.WorkflowTypeIds )
                            {
                                person.LaunchWorkflow( wfId, person.FullName, workflowParameters );
                            }
                        }
                    }
                }

                // Format the result message
                return $"{recordsProcessed:N0} children were processed; {recordsUpdated:N0} were moved to their own family.";
            }
            catch ( Exception ex )
            {
                // Log exception and return the exception messages.
                HttpContext context2 = HttpContext.Current;
                ExceptionLogService.LogException( ex, context2 );

                return ex.Messages().AsDelimited( "; " );
            }
        }

        #endregion

        private List<int> GetPeopleWhoContributed( bool enabled, int periodInDays, RockContext rockContext )
        {
            if ( enabled )
            {
                var contributionType = DefinedValueCache.Read( SystemGuid.DefinedValue.TRANSACTION_TYPE_CONTRIBUTION.AsGuid() );
                if ( contributionType != null )
                {
                    var startDate = RockDateTime.Now.AddDays( -periodInDays );
                    return new FinancialTransactionService( rockContext )
                        .Queryable().AsNoTracking()
                        .Where( t =>
                            t.TransactionTypeValueId == contributionType.Id &&
                            t.TransactionDateTime.HasValue &&
                            t.TransactionDateTime.Value >= startDate &&
                            t.AuthorizedPersonAliasId.HasValue )
                        .Select( a => a.AuthorizedPersonAlias.PersonId )
                        .Distinct()
                        .ToList();
                }
            }
            return new List<int>();
        }

        private List<int> GetPeopleWhoAttendedServiceGroup( bool enabled, int periodInDays, RockContext rockContext )
        {
            if ( enabled )
            {
                var startDate = RockDateTime.Now.AddDays( -periodInDays );

                return new AttendanceService( rockContext )
                    .Queryable().AsNoTracking()
                    .Where( a =>
                        a.Group != null &&
                        a.Group.GroupType != null &&
                        a.Group.GroupType.AttendanceCountsAsWeekendService &&
                        a.StartDateTime >= startDate &&
                        a.DidAttend.HasValue &&
                        a.DidAttend.Value == true &&
                        a.PersonAlias != null )
                    .Select( a => a.PersonAlias.PersonId )
                    .Distinct()
                    .ToList();
            }

            return new List<int>();
        }

        private List<int> GetPeopleWhoAttendedGroupType( bool enabled, List<int> groupTypeIds, int periodInDays, RockContext rockContext )
        {
            if ( enabled )
            {
                var startDate = RockDateTime.Now.AddDays( -periodInDays );

                return new AttendanceService( rockContext )
                    .Queryable().AsNoTracking()
                    .Where( a =>
                        a.Group != null &&
                        groupTypeIds.Contains( a.Group.GroupTypeId ) &&
                        a.StartDateTime >= startDate &&
                        a.DidAttend.HasValue &&
                        a.DidAttend.Value == true &&
                        a.PersonAlias != null )
                    .Select( a => a.PersonAlias.PersonId )
                    .Distinct()
                    .ToList();
            }

            return new List<int>();
        }


        private List<int> GetPeopleWhoSubmittedPrayerRequest( bool enabled, int periodInDays, RockContext rockContext )
        {
            if ( enabled )
            {
                var startDate = RockDateTime.Now.AddDays( -periodInDays );

                return new PrayerRequestService( rockContext )
                    .Queryable().AsNoTracking()
                    .Where( a =>
                        a.EnteredDateTime >= startDate &&
                        a.RequestedByPersonAlias != null )
                    .Select( a => a.RequestedByPersonAlias.PersonId )
                    .Distinct()
                    .ToList();
            }

            return new List<int>();
        }

        private List<int> GetPeopleWithPersonAttributUpdates( bool enabled, List<int> attributeIds, int periodInDays, RockContext rockContext )
        {
            if ( enabled && attributeIds != null && attributeIds.Any() )
            {
                var startDate = RockDateTime.Now.AddDays( -periodInDays );

                return new AttributeValueService( rockContext )
                    .Queryable().AsNoTracking()
                    .Where( a =>
                        a.ModifiedDateTime.HasValue && 
                        a.ModifiedDateTime.Value >= startDate &&
                        attributeIds.Contains( a.AttributeId ) &&
                        a.EntityId.HasValue )
                    .Select( a => a.EntityId.Value )
                    .Distinct()
                    .ToList();
            }

            return new List<int>();
        }

        private List<int> GetPeopleWithInteractions( bool enabled, List<Utility.Settings.DataAutomation.InteractionItem> interactionItems, RockContext rockContext )
        {
            if ( enabled && interactionItems != null && interactionItems.Any() )
            {
                var personIdList = new List<int>();

                foreach ( var interactionItem in interactionItems )
                {
                    var startDate = RockDateTime.Now.AddDays( -interactionItem.LastInteractionDays );

                    personIdList.AddRange( new InteractionService( rockContext )
                        .Queryable().AsNoTracking()
                        .Where( a =>
                            a.InteractionDateTime >= startDate &&
                            a.InteractionComponent.Channel.Guid == interactionItem.Guid &&
                            a.PersonAlias != null )
                        .Select( a => a.PersonAlias.PersonId )
                        .Distinct()
                        .ToList() );
                }

                return personIdList.Distinct().ToList();
            }

            return new List<int>();
        }

        private List<int> GetPeopleInDataView( bool enabled, int? dataviewId, RockContext rockContext )
        {
            if ( enabled && dataviewId.HasValue )
            {
                var dataView = new DataViewService( rockContext ).Get( dataviewId.Value );
                if ( dataView != null )
                {
                    List<string> errorMessages = new List<string>();
                    var qry = dataView.GetQuery( null, null, out errorMessages );
                    if ( qry != null )
                    {
                        return qry
                            .Select( e => e.Id )
                            .ToList();
                    }
                }
            }

            return new List<int>();
        }


    }

}