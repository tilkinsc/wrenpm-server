using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;

interface IPackageService
{
	Task<PackageListResult> ListPackagesAsync(string? search, int page, int pageSize);
	Task<List<string>> GetPackageVersionsAsync(string name);
	Task<DownloadResult?> GetPackageAsync(string name, string version);
	Task<PackageMetadataDTO?> GetPackageMetadataAsync(string name, string version);
	Task<UploadResult> UploadPackageAsync(PackageUploadRequest request);
	Task<bool> DeletePackageAsync(string name, string version);
	Task TrackDownloadAsync(string name, string version);
	Task<PackageStats?> GetPackageStatsAsync(string name);
}

class PackageService : IPackageService
{
	private readonly IMongoCollection<PackageMetadata> _collection;
	private readonly ILFSService _fileStorage;
	private readonly ISecurityService _security;
	private readonly ILogger<PackageService> _logger;
	private readonly FilterDefinitionBuilder<PackageMetadata> _filterBuilder;
	private readonly ProjectionDefinition<PackageMetadata, PackageMetadataDTO> _dtoProjection;

	public PackageService(
		IMongoCollection<PackageMetadata> collection,
		ILFSService fileStorage,
		ISecurityService security,
		ILogger<PackageService> logger)
	{
		_collection = collection;
		_fileStorage = fileStorage;
		_security = security;
		_logger = logger;
		_filterBuilder = Builders<PackageMetadata>.Filter;

		_dtoProjection = Builders<PackageMetadata>.Projection.Expression(p => new PackageMetadataDTO
		{
			Name = p.Name,
			Version = p.Version,
			Description = p.Description,
			Tags = p.Tags,
			Author = p.Author,
			CreatedAt = p.CreatedAt,
			DownloadCount = p.DownloadCount,
			FileSize = p.FileSize
		});
	}

	public async Task<PackageListResult> ListPackagesAsync(string? search, int page, int pageSize)
	{
		var filter = string.IsNullOrEmpty(search)
			? _filterBuilder.Empty
			: _filterBuilder.Or(
				_filterBuilder.Regex("name", new BsonRegularExpression(Regex.Escape(search), "i")),
				_filterBuilder.Regex("description", new BsonRegularExpression(Regex.Escape(search), "i")),
				_filterBuilder.AnyEq("tags", search)
			);

		var totalCount = await _collection.CountDocumentsAsync(filter);
		var packages = await _collection.Find(filter)
			.Skip((page - 1) * pageSize)
			.Limit(pageSize)
			.Project(_dtoProjection)
			.ToListAsync();

		return new PackageListResult
		{
			Packages = packages,
			TotalCount = (int)totalCount,
			Page = page,
			PageSize = pageSize
		};
	}

	public async Task<List<string>> GetPackageVersionsAsync(string name)
	{
		return await _collection.Find(p => p.Name == name)
			.SortByDescending(p => p.CreatedAt)
			.Project(p => p.Version)
			.ToListAsync();
	}

	public async Task<DownloadResult?> GetPackageStreamAsync(string name, string version)
	{
		var stream = await _fileStorage.GetPackageStreamAsync(name, version);
		if (stream == null) return null;

		return new DownloadResult
		{
			Stream = stream,
			FileName = $"{name}-{version}.zip"
		};
	}

	public async Task<DownloadResult?> GetPackageAsync(string name, string version)
	{
		return await GetPackageStreamAsync(name, version);
	}

	public async Task<PackageMetadataDTO?> GetPackageMetadataAsync(string name, string version)
	{
		return await _collection
			.Find(p => p.Name == name && p.Version == version)
			.Project(_dtoProjection)
			.FirstOrDefaultAsync();
	}

	public async Task<UploadResult> UploadPackageAsync(PackageUploadRequest request)
	{
		try
		{
			// Check if version already exists
			var existing = await _collection.Find(p => p.Name == request.Name && p.Version == request.Version).AnyAsync();
			if (existing)
			{
				return new UploadResult { IsSuccess = false, Error = "This version already exists" };
			}

			// Store file and compute checksum
			string checksum;
			using (var stream = request.File!.OpenReadStream())
			{
				checksum = _security.ComputeChecksum(stream);
				stream.Position = 0;
				await _fileStorage.StorePackageAsync(request.Name, request.Version, stream);
			}

			// Store metadata
			var metadata = new PackageMetadata
			{
				Id = ObjectId.GenerateNewId(),
				Name = request.Name,
				Version = request.Version,
				Description = request.Description,
				Tags = request.Tags,
				Author = request.Author,
				CreatedAt = DateTime.UtcNow,
				DownloadCount = 0,
				Checksum = checksum,
				FileSize = request.File.Length
			};

			await _collection.InsertOneAsync(metadata);

			return new UploadResult
			{
				IsSuccess = true,
				PackageId = metadata.Id.ToString()
			};
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to upload package {Name}@{Version}", request.Name, request.Version);
			return new UploadResult { IsSuccess = false, Error = "Upload failed" };
		}
	}

	public async Task<bool> DeletePackageAsync(string name, string version)
	{
		var deleteResult = await _collection.DeleteOneAsync(p => p.Name == name && p.Version == version);
		if (deleteResult.DeletedCount > 0)
		{
			await _fileStorage.DeletePackageAsync(name, version);
			return true;
		}
		return false;
	}

	public async Task TrackDownloadAsync(string name, string version)
	{
		var update = Builders<PackageMetadata>.Update.Inc(p => p.DownloadCount, 1);
		await _collection.UpdateOneAsync(p => p.Name == name && p.Version == version, update);
	}

	public async Task<PackageStats?> GetPackageStatsAsync(string name)
	{
		var packages = await _collection.Find(p => p.Name == name).ToListAsync();
		if (packages.Count == 0) return null;

		return new PackageStats
		{
			Name = name,
			TotalDownloads = packages.Sum(p => p.DownloadCount),
			VersionCount = packages.Count,
			LastUpdated = packages.Max(p => p.CreatedAt),
			VersionDownloads = packages.ToDictionary(p => p.Version, p => p.DownloadCount)
		};
	}
}
