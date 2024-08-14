// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.CosmosDB;
using Azure.ResourceManager.CosmosDB.Models;
using EnsureThat;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.CosmosDb.Core.Configs;
using Microsoft.Health.Fhir.CosmosDb.Core.Features.Storage;
using Microsoft.Health.Fhir.CosmosDb.Core.Features.Storage.StoredProcedures;
using Polly;

namespace Microsoft.Health.Fhir.CosmosDb.Initialization.Features.Storage;

public class ArmSdkCollectionSetup : ICollectionSetup
{
    private readonly ILogger<ArmSdkCollectionSetup> _logger;
    private readonly ArmClient _armClient;
    private readonly IOptionsMonitor<CosmosCollectionConfiguration> _cosmosCollectionConfiguration;
    private readonly CosmosDataStoreConfiguration _cosmosDataStoreConfiguration;
    private readonly IEnumerable<IStoredProcedureMetadata> _storeProceduresMetadata;
    private readonly ResourceIdentifier _resourceIdentifier;
    private CosmosDBSqlDatabaseResource _database;
    private AzureLocation? _location;
    private readonly CosmosDBAccountResource _account;

    public ArmSdkCollectionSetup(
        IOptionsMonitor<CosmosCollectionConfiguration> cosmosCollectionConfiguration,
        CosmosDataStoreConfiguration cosmosDataStoreConfiguration,
        IConfiguration genericConfiguration,
        IEnumerable<IStoredProcedureMetadata> storedProcedures,
        ILogger<ArmSdkCollectionSetup> logger)
    {
        EnsureArg.IsNotNull(storedProcedures, nameof(storedProcedures));

        _cosmosCollectionConfiguration = EnsureArg.IsNotNull(cosmosCollectionConfiguration, nameof(cosmosCollectionConfiguration));
        _cosmosDataStoreConfiguration = EnsureArg.IsNotNull(cosmosDataStoreConfiguration, nameof(cosmosDataStoreConfiguration));
        _storeProceduresMetadata = EnsureArg.IsNotNull(storedProcedures, nameof(storedProcedures));
        _logger = EnsureArg.IsNotNull(logger, nameof(logger));

        _armClient = new ArmClient(new DefaultAzureCredential());
        var dataStoreResourceId = genericConfiguration.GetValue("FhirServer:ResourceManager:DataStoreResourceId", string.Empty);
        _resourceIdentifier = ResourceIdentifier.Parse(dataStoreResourceId);
        _account = _armClient.GetCosmosDBAccountResource(_resourceIdentifier);
    }

    private CosmosDBSqlDatabaseResource Database
    {
        get
        {
            if (_database != null)
            {
                return _database;
            }

            _database = _account.GetCosmosDBSqlDatabase(_cosmosDataStoreConfiguration.DatabaseId);
            return _database;
        }
    }

    private AzureLocation Location
    {
        get
        {
            if (_location.HasValue)
            {
                return _location.Value;
            }

            _location = _account.Get().Value.Data.Location;
            return _location.Value;
        }
    }

    private string CollectionId
    {
        get
        {
            return _cosmosCollectionConfiguration.Get(Core.Constants.CollectionConfigurationName).CollectionId;
        }
    }

    public async Task CreateDatabaseAsync(AsyncPolicy retryPolicy, CancellationToken cancellationToken)
    {
        CosmosDBAccountResource account = _armClient.GetCosmosDBAccountResource(_resourceIdentifier);
        CosmosDBSqlDatabaseCollection databaseCollection = account.GetCosmosDBSqlDatabases();

        _logger.LogInformation("Checking if '{DatabaseId}' exists.", _cosmosDataStoreConfiguration.DatabaseId);

        if (!(await databaseCollection.ExistsAsync(_cosmosDataStoreConfiguration.DatabaseId, cancellationToken)).Value)
        {
            _logger.LogInformation("Database '{DatabaseId}' was not found, creating.", _cosmosDataStoreConfiguration.DatabaseId);

            await databaseCollection.CreateOrUpdateAsync(
                WaitUntil.Completed,
                _cosmosDataStoreConfiguration.DatabaseId,
                new CosmosDBSqlDatabaseCreateOrUpdateContent(
                    Location,
                    new CosmosDBSqlDatabaseResourceInfo(_cosmosDataStoreConfiguration.DatabaseId)),
                cancellationToken);
        }
        else
        {
            _logger.LogInformation("Database '{DatabaseId}' found.", _cosmosDataStoreConfiguration.DatabaseId);
        }
    }

    public async Task CreateCollectionAsync(IEnumerable<ICollectionInitializer> collectionInitializers, AsyncPolicy retryPolicy, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Checking if '{CollectionId}' exists.", CollectionId);

        NullableResponse<CosmosDBSqlContainerResource> containers = await Database.GetCosmosDBSqlContainers().GetIfExistsAsync(CollectionId, cancellationToken);

        if (!containers.HasValue)
        {
            _logger.LogInformation("Collection '{CollectionId}' was not found, creating.", CollectionId);
            CosmosDBSqlContainerResourceInfo containerResourceInfo = GetContainerResourceInfo();

            var content = new CosmosDBSqlContainerCreateOrUpdateContent(
                Location,
                containerResourceInfo);

            CosmosDBSqlContainerCollection containerCollection = Database.GetCosmosDBSqlContainers();
            await containerCollection.CreateOrUpdateAsync(WaitUntil.Completed, CollectionId, content, cancellationToken);

            containers = await Database.GetCosmosDBSqlContainerAsync(CollectionId, cancellationToken);

            var throughput =
                _cosmosCollectionConfiguration.Get(Core.Constants.CollectionConfigurationName)
                    .InitialCollectionThroughput;

            if (throughput.HasValue)
            {
                _logger.LogInformation("Updating container throughput to '{Throughput}' RUs.", throughput);
                await containers.Value
                    .GetCosmosDBSqlContainerThroughputSetting()
                    .CreateOrUpdateAsync(
                        WaitUntil.Completed,
                        new ThroughputSettingsUpdateData(
                            Location,
                            new ThroughputSettingsResourceInfo
                            {
                                Throughput = throughput,
                            }),
                        cancellationToken);
            }
        }
        else
        {
            _logger.LogInformation("Collection '{CollectionId}' found.", CollectionId);
        }

        var meta = _storeProceduresMetadata.ToList();

        CosmosDBSqlStoredProcedureCollection cosmosDbSqlStoredProcedures = containers.Value.GetCosmosDBSqlStoredProcedures();
        var existing = cosmosDbSqlStoredProcedures.Select(x => x.Data.Resource.StoredProcedureName).ToList();

        foreach (IStoredProcedureMetadata storedProc in meta)
        {
            if (!existing.Contains(storedProc.FullName))
            {
                _logger.LogInformation("Installing StoredProc '{StoredProcFullName}'.", storedProc.FullName);

                var cosmosDbSqlStoredProcedureResource = new CosmosDBSqlStoredProcedureResourceInfo(storedProc.FullName)
                {
                    Body = storedProc.ToStoredProcedureProperties().Body,
                };

                var storedProcedureCreateOrUpdateContent = new CosmosDBSqlStoredProcedureCreateOrUpdateContent(Location, cosmosDbSqlStoredProcedureResource);
                await cosmosDbSqlStoredProcedures.CreateOrUpdateAsync(WaitUntil.Completed, storedProc.FullName, storedProcedureCreateOrUpdateContent, cancellationToken);
            }
        }
    }

    public async Task UpdateFhirCollectionSettingsAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Updating collection settings.");

        CosmosDBSqlContainerResourceInfo containerResourceInfo = GetContainerResourceInfo();

        var content = new CosmosDBSqlContainerCreateOrUpdateContent(
            Location,
            containerResourceInfo);

        CosmosDBSqlContainerCollection containers = Database.GetCosmosDBSqlContainers();
        await containers.CreateOrUpdateAsync(WaitUntil.Completed, CollectionId, content, cancellationToken);
    }

    private CosmosDBSqlContainerResourceInfo GetContainerResourceInfo()
    {
        return new CosmosDBSqlContainerResourceInfo(CollectionId)
        {
            PartitionKey = new CosmosDBContainerPartitionKey
            {
                Paths = { $"/{KnownDocumentProperties.PartitionKey}" },
                Kind = CosmosDBPartitionKind.Hash,
            },
            IndexingPolicy = CreateCosmosDbIndexingPolicy(),
            DefaultTtl = -1,
        };
    }

    private static CosmosDBIndexingPolicy CreateCosmosDbIndexingPolicy()
    {
        var indexingPolicy = new CosmosDBIndexingPolicy
        {
            IsAutomatic = true, // Enable automatic indexing
            IndexingMode = CosmosDBIndexingMode.Consistent, // Choose indexing mode (Consistent or Lazy)
            IncludedPaths =
            {
                new CosmosDBIncludedPath
                {
                    Path = "/*", // Include all properties
                },
            },
            ExcludedPaths =
            {
                new CosmosDBExcludedPath
                {
                    Path = $"/{"rawResource"}/*", // Exclude properties under /excludedPath
                },
                new CosmosDBExcludedPath
                {
                    Path = "/\"_etag\"/?",
                },
            },
        };
        return indexingPolicy;
    }
}
