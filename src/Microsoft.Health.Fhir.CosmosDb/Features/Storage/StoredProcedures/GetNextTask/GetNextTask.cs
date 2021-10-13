﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Microsoft.Azure.Cosmos.Scripts;
using Microsoft.Health.Fhir.CosmosDb.Features.Storage.Operations.Task;

namespace Microsoft.Health.Fhir.CosmosDb.Features.Storage.StoredProcedures.GetNextTask
{
    internal class GetNextTask : StoredProcedureBase
    {
        public async Task<StoredProcedureExecuteResponse<IReadOnlyCollection<CosmosTaskInfoWrapper>>> ExecuteAsync(
            Scripts client,
            string queueId,
            ushort count,
            int taskHeartbeatTimeoutThresholdInSeconds,
            CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(client, nameof(client));

            return await ExecuteStoredProc<IReadOnlyCollection<CosmosTaskInfoWrapper>>(
                client,
                CosmosDbTaskConstants.TaskPartitionKey,
                cancellationToken,
                queueId,
                count,
                taskHeartbeatTimeoutThresholdInSeconds);
        }
    }
}
