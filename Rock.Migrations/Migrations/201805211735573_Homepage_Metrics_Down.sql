﻿
DELETE FROM [dbo].[MetricValuePartition]
WHERE [Guid] IN ('d72e40cf-2e85-44b9-b864-140a37e85afd','465d1cad-0798-4e3c-8671-b53c032a0eac', 'd7af2f13-8f7d-46a0-b91a-424c6b080afb')

DELETE FROM [dbo].[MetricValue]
WHERE [GUID] IN ('34325795-9016-47e9-a9d9-6283d1a84275', '90cd5a83-3079-4656-b7ce-bfa21055c980', '932479dd-9612-4d07-b9cd-9227976cf5dd')

DELETE FROM [dbo].[MetricPartition]
WHERE [GUID] IN ('20bd0c1e-2faf-41ea-b443-839cbe2dce9a', 'f879279d-3484-4f58-a16d-f64bdb277358', 'bd1bd405-e6f0-439b-90b6-7bd97c76b637' )

DELETE FROM [dbo].[MetricCategory]
WHERE [GUID] IN	('aa35efa8-97cd-4124-9f7a-0032af56ec51', 'd7323374-0745-405e-a5be-4975783601e0', '67686294-87ee-45f1-abf6-4273c9b524ca' )

DELETE FROM [dbo].[Metric]
WHERE [GUID] IN	('ecb1b552-9a3d-46fc-952b-d57dbc4a329d', '491061b7-1834-44da-8ea1-bb73b2d52ad3', 'f0a24208-f8ac-4e04-8309-1a276885f6a6', '6a1e1a1b-a636-4e12-b90c-d7fd1bdae764', '073add0c-b1f3-43ab-8360-89a1ce05a95d')