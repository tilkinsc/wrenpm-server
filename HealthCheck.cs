
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Bson;
using MongoDB.Driver;

class StorageHealthCheck(ILFSService storage, ILogger<StorageHealthCheck> logger) : IHealthCheck
{
	private readonly ILFSService _storage = storage;
	private readonly ILogger<StorageHealthCheck> _logger = logger;

	public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
	{
		try
		{
			// Test storage by checking if we can write/read a test file
			using var testStream = new MemoryStream("healthcheck"u8.ToArray());
			await _storage.StorePackageAsync("_healthcheck", "1.0.0", testStream);

			var retrievedStream = await _storage.GetPackageStreamAsync("_healthcheck", "1.0.0");
			if (retrievedStream == null)
			{
				return HealthCheckResult.Degraded("Storage write succeeded but read failed");
			}

			retrievedStream.Dispose();
			await _storage.DeletePackageAsync("_healthcheck", "1.0.0");

			return HealthCheckResult.Healthy("Storage is working correctly");
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Storage health check failed");
			return HealthCheckResult.Unhealthy("Storage is not working", ex);
		}
	}
}

class DatabaseHealthCheck(IMongoDatabase database, ILogger<DatabaseHealthCheck> logger) : IHealthCheck
{
	private readonly IMongoDatabase _database = database;
	private readonly ILogger<DatabaseHealthCheck> _logger = logger;

	public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
	{
		try
		{
			await _database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: cancellationToken);
			return HealthCheckResult.Healthy("MongoDB is responsive");
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "MongoDB health check failed");
			return HealthCheckResult.Unhealthy("MongoDB is not responsive", ex);
		}
	}
}

