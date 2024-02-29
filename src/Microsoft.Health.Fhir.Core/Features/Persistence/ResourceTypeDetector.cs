// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Text;
using System.Text.Json;

namespace Microsoft.Health.Fhir.Core.Features.Persistence;

internal static class ResourceTypeDetector
{
    public static bool TryPeek(string resourceJson, out string resourceType)
    {
        resourceType = null;

        var span = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(resourceJson));
        var reader = new Utf8JsonReader(span.Span);

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName && reader.GetString() == "resourceType")
            {
                // Move to the value associated with the key "resourceType"
                reader.Read();

                // Assuming the value is a string
                resourceType = reader.GetString();
                return true;
            }
        }

        return false;
    }
}
