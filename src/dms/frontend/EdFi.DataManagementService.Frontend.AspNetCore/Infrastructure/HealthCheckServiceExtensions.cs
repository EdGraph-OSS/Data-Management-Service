// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Core.Configuration;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

public class ApplicationHealthCheck(ILogger<ApplicationHealthCheck> logger) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            return Task.FromResult(HealthCheckResult.Healthy("Application is up and running"));
        }
        catch (Exception e)
        {
            logger.LogError(e, e.Message);
            return Task.FromResult(HealthCheckResult.Unhealthy(description: e.Message));
        }
    }
}

/// <summary>
/// Checks that a loaded data store's database accepts connections.
/// </summary>
/// <remarks>
/// The connection string is resolved on every check rather than once at construction. This check is
/// a singleton, and with multi-tenancy the data stores load on demand, so a pod that starts with no
/// tenants would otherwise keep the empty value it saw at its first probe for its whole lifetime.
/// With no data store to probe the result is <see cref="HealthStatus.Degraded"/>, not Healthy: the
/// state is expected at multi-tenant startup, but a cache refresh can also replace a working
/// tenant's data stores with an empty list, and that must stay visible in the report. Degraded maps
/// to HTTP 200, so it does not fail probes. Data stores that are loaded but have no connection string
/// are a misconfiguration, not a wait, so that state is <see cref="HealthStatus.Unhealthy"/> with
/// its own description.
/// In multi-tenant mode the check connects to nothing and reports Healthy. Each data store belongs to a
/// tenant and is reached with that tenant's own login; probing one of them made every liveness and
/// readiness probe a login into an arbitrary tenant's database. After that tenant was deleted the probes
/// became failed logins against a dropped database, which Defender for Cloud reports as a brute-force
/// attack, and one tenant's database could fail the probes of a pod that serves all tenants.
/// </remarks>
public class DbHealthCheck(
    IConnectionStringProvider connectionStringProvider,
    string providerName,
    ILogger<DbHealthCheck> logger,
    bool multiTenancy = false
) : IHealthCheck
{
    internal const string MultiTenantDescription =
        "Multi-tenant mode: tenant databases are not probed; each is reached with its tenant's login per request.";

    internal const string NoDataStoresDescription =
        "No data stores are loaded yet, so there is no database to check.";

    internal static string NoConnectionStringDescription(int dataStoreCount) =>
        $"{dataStoreCount} data store(s) are loaded, but none has a connection string. "
        + "Check the data store configuration in the Configuration Service.";

    private readonly IConnectionStringProvider _connectionStringProvider =
        connectionStringProvider ?? throw new ArgumentNullException(nameof(connectionStringProvider));
    private readonly ILogger<DbHealthCheck> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    // Validated here rather than on first connection: with no data store loaded the check never
    // reaches CreateConnection, so an unsupported value would otherwise stay hidden until one loads.
    private readonly string _providerName = ValidateProviderName(providerName);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    )
    {
        if (multiTenancy)
        {
            return HealthCheckResult.Healthy(MultiTenantDescription);
        }

        HealthCheckTarget target = _connectionStringProvider.GetHealthCheckTarget();

        switch (target.Status)
        {
            // Multi-tenant startup with zero tenants is a supported state, not a database fault:
            // data stores load when tenants are created and requests arrive.
            case HealthCheckTargetStatus.NoDataStoresLoaded:
                return HealthCheckResult.Degraded(NoDataStoresDescription);
            case HealthCheckTargetStatus.NoConnectionString:
                return HealthCheckResult.Unhealthy(NoConnectionStringDescription(target.DataStoreCount));
        }

        string connectionString = target.ConnectionString!;

        try
        {
            await using var connection = CreateConnection(_providerName, connectionString);
            _logger.LogDebug("Attempting to open a connection to the database.");

            await connection.OpenAsync(cancellationToken);

            _logger.LogDebug("Database connection established successfully.");

            return HealthCheckResult.Healthy("Database connection is healthy.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Database connection is unhealthy.", ex);
        }
    }

    private static string ValidateProviderName(string providerName)
    {
        ArgumentNullException.ThrowIfNull(providerName);
        if (
            !providerName.Equals("postgresql", StringComparison.OrdinalIgnoreCase)
            && !providerName.Equals("mssql", StringComparison.OrdinalIgnoreCase)
        )
        {
            throw new ArgumentException(
                $"Unsupported provider: '{providerName}'. Expected 'postgresql' or 'mssql'.",
                nameof(providerName)
            );
        }
        return providerName;
    }

    private static DbConnection CreateConnection(string providerName, string connectionString)
    {
        return providerName.ToLowerInvariant() switch
        {
            "postgresql" => new NpgsqlConnection(connectionString),
            "mssql" => new SqlConnection(connectionString),
            _ => throw new ArgumentException($"Unsupported provider: {providerName}"),
        };
    }
}
