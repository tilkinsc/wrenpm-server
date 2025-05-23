using MongoDB.Bson;
using MongoDB.Driver;
using System.Text.Json;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Configuration
var config = builder.Configuration;
var packageConfig = config.GetSection("PackageManager").Get<PackageManagerConfig>() ?? new();
var mongoConfig = config.GetSection("MongoDB").Get<MongoDbConfig>() ?? new();

// Logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddJsonConsole();

// Services
builder.Services.Configure<PackageManagerConfig>(config.GetSection("PackageManager"));
builder.Services.Configure<MongoDbConfig>(config.GetSection("MongoDB"));

// MongoDB setup with error handling
try
{
	var mongoClient = new MongoClient(mongoConfig.ConnectionString);
	var database = mongoClient.GetDatabase(mongoConfig.DatabaseName);
	var packagesCollection = database.GetCollection<PackageMetadata>("packages");
	
	builder.Services.AddSingleton(mongoClient);
	builder.Services.AddSingleton(database);
	builder.Services.AddSingleton(packagesCollection);
}
catch (Exception ex)
{
	throw new InvalidOperationException("Failed to connect to MongoDB", ex);
}

// Services registration
builder.Services.AddSingleton<IPackageService, PackageService>();
builder.Services.AddSingleton<ILFSService, LFSService>();
builder.Services.AddSingleton<IValidationService, ValidationService>();
builder.Services.AddSingleton<ISecurityService, SecurityService>();

// API Documentation
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
	c.SwaggerDoc("v1", new() { Title = "Package Manager API", Version = "v1" });
});

// Rate limiting
builder.Services.AddRateLimiter(options =>
{
	options.AddFixedWindowLimiter("api", config =>
	{
		config.PermitLimit = packageConfig.RateLimit.RequestsPerMinute;
		config.Window = TimeSpan.FromMinutes(1);
		config.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
		config.QueueLimit = 10;
	});
	
	options.AddFixedWindowLimiter("upload", config =>
	{
		config.PermitLimit = packageConfig.RateLimit.UploadsPerHour;
		config.Window = TimeSpan.FromHours(1);
		config.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
		config.QueueLimit = 5;
	});
});

// Health checks
builder.Services
	.AddHealthChecks()
	.AddCheck<DatabaseHealthCheck>("database")
	.AddCheck<StorageHealthCheck>("storage");

// CORS
builder.Services.AddCors(options =>
{
	options.AddPolicy("PackageManagerPolicy", policy =>
	{
		policy.WithOrigins(packageConfig.AllowedOrigins)
			.AllowAnyHeader()
			.AllowAnyMethod();
	});
});

var app = builder.Build();

// Middleware pipeline
if (app.Environment.IsDevelopment())
{
	app.UseSwagger();
	app.UseSwaggerUI();
}

app.UseCors("PackageManagerPolicy");
app.UseRateLimiter();

// Global exception handling
app.UseExceptionHandler(errorApp =>
{
	errorApp.Run(async context =>
	{
		var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
		var feature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
		
		if (feature?.Error != null)
		{
			logger.LogError(feature.Error, "Unhandled exception occurred");
		}
		
		context.Response.StatusCode = 500;
		context.Response.ContentType = "application/json";
		
		var response = new { error = "An internal server error occurred" };
		await context.Response.WriteAsync(JsonSerializer.Serialize(response));
	});
});

// Health check endpoint
app.MapHealthChecks("/health");

// API Endpoints
var api = app.MapGroup("/api/v1").RequireRateLimiting("api");

// List packages with search & pagination
api.MapGet("/packages", async (
	IPackageService packageService,
	ILogger<Program> logger,
	string? search = null,
	int page = 1,
	int pageSize = 10) =>
{
	try
	{
		logger.LogInformation("Listing packages - Search: {Search}, Page: {Page}, PageSize: {PageSize}", 
			search, page, pageSize);
		
		if (page < 1) page = 1;
		if (pageSize < 1 || pageSize > 100) pageSize = 10;
		
		var result = await packageService.ListPackagesAsync(search, page, pageSize);
		return Results.Ok(result);
	}
	catch (Exception ex)
	{
		logger.LogError(ex, "Error listing packages");
		return Results.Problem("Failed to retrieve packages");
	}
});

// List versions for a package
api.MapGet("/packages/{name}", async (
	IPackageService packageService,
	IValidationService validation,
	ILogger<Program> logger,
	string name) =>
{
	try
	{
		if (!validation.IsValidPackageName(name))
		{
			logger.LogWarning("Invalid package name requested: {Name}", name);
			return Results.BadRequest("Invalid package name");
		}
		
		logger.LogInformation("Listing versions for package: {Name}", name);
		var versions = await packageService.GetPackageVersionsAsync(name);
		
		return versions.Count != 0 ? Results.Ok(versions) : Results.NotFound();
	}
	catch (Exception ex)
	{
		logger.LogError(ex, "Error getting package versions for {Name}", name);
		return Results.Problem("Failed to retrieve package versions");
	}
});

// Download package
api.MapGet("/packages/{name}/{version}", async (
	IPackageService packageService,
	IValidationService validation,
	ILogger<Program> logger,
	string name,
	string version) =>
{
	try
	{
		if (!validation.IsValidPackageName(name) || !validation.IsValidVersion(version))
		{
			logger.LogWarning("Invalid download request - Name: {Name}, Version: {Version}", name, version);
			return Results.BadRequest("Invalid package name or version");
		}
		
		logger.LogInformation("Download requested - Package: {Name}, Version: {Version}", name, version);
		
		var downloadResult = await packageService.GetPackageAsync(name, version);
		if (downloadResult == null)
		{
			logger.LogWarning("Package not found - Name: {Name}, Version: {Version}", name, version);
			return Results.NotFound();
		}
		
		// Track download
		await packageService.TrackDownloadAsync(name, version);
		
		return Results.File(downloadResult.Stream, "application/zip", downloadResult.FileName);
	}
	catch (Exception ex)
	{
		logger.LogError(ex, "Error downloading package {Name}@{Version}", name, version);
		return Results.Problem("Failed to download package");
	}
});

// Get package metadata
api.MapGet("/packages/{name}/{version}/metadata", async (
	IPackageService packageService,
	IValidationService validation,
	ILogger<Program> logger,
	string name,
	string version) =>
{
	try
	{
		if (!validation.IsValidPackageName(name) || !validation.IsValidVersion(version))
		{
			return Results.BadRequest("Invalid package name or version");
		}
		
		var metadata = await packageService.GetPackageMetadataAsync(name, version);
		return metadata != null ? Results.Ok(metadata) : Results.NotFound();
	}
	catch (Exception ex)
	{
		logger.LogError(ex, "Error getting metadata for {Name}@{Version}", name, version);
		return Results.Problem("Failed to retrieve package metadata");
	}
});

// Upload package (with stricter rate limiting)
api.MapPost("/packages", async (
	IPackageService packageService,
	IValidationService validation,
	ISecurityService security,
	ILogger<Program> logger,
	HttpRequest request) =>
{
	try
	{
		// Authenticate request
		var authResult = await security.AuthenticateAsync(request);
		if (!authResult.IsSuccess)
		{
			logger.LogWarning("Unauthorized upload attempt from {IP}", request.HttpContext.Connection.RemoteIpAddress);
			return Results.Unauthorized();
		}
		
		// Validate content length
		if (request.ContentLength > packageConfig.MaxPackageSize)
		{
			logger.LogWarning("Package too large: {Size} bytes", request.ContentLength);
			return Results.BadRequest($"Package size exceeds maximum allowed size of {packageConfig.MaxPackageSize} bytes");
		}
		
		var form = await request.ReadFormAsync();
		var uploadRequest = new PackageUploadRequest
		{
			Name = form["name"].ToString(),
			Version = form["version"].ToString(),
			Description = form["description"].ToString(),
			Tags = form["tags"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries),
			File = form.Files["file"],
			Author = authResult.User
		};
		
		// Validate upload request
		var validationResult = validation.ValidateUploadRequest(uploadRequest);
		if (!validationResult.IsValid)
		{
			logger.LogWarning("Invalid upload request: {Errors}", string.Join(", ", validationResult.Errors));
			return Results.BadRequest(new { errors = validationResult.Errors });
		}
		
		logger.LogInformation("Processing upload for {Name}@{Version} by {Author}", 
			uploadRequest.Name, uploadRequest.Version, uploadRequest.Author);
		
		var result = await packageService.UploadPackageAsync(uploadRequest);
		if (!result.IsSuccess)
		{
			logger.LogWarning("Upload failed for {Name}@{Version}: {Error}", 
				uploadRequest.Name, uploadRequest.Version, result.Error);
			return Results.BadRequest(new { error = result.Error });
		}
		
		logger.LogInformation("Package uploaded successfully: {Name}@{Version}", 
			uploadRequest.Name, uploadRequest.Version);
		
		return Results.Ok(new { message = "Package uploaded successfully", packageId = result.PackageId });
	}
	catch (Exception ex)
	{
		logger.LogError(ex, "Error processing package upload");
		return Results.Problem("Failed to upload package");
	}
}).RequireRateLimiting("upload");

// Delete package version (authenticated)
api.MapDelete("/packages/{name}/{version}", async (
	IPackageService packageService,
	IValidationService validation,
	ISecurityService security,
	ILogger<Program> logger,
	HttpRequest request,
	string name,
	string version) =>
{
	try
	{
		var authResult = await security.AuthenticateAsync(request);
		if (!authResult.IsSuccess)
		{
			return Results.Unauthorized();
		}
		
		if (!validation.IsValidPackageName(name) || !validation.IsValidVersion(version))
		{
			return Results.BadRequest("Invalid package name or version");
		}
		
		var hasPermission = await security.CanDeletePackageAsync(authResult.User, name);
		if (!hasPermission)
		{
			logger.LogWarning("User {User} attempted to delete package {Name} without permission", 
				authResult.User, name);
			return Results.Forbid();
		}
		
		var result = await packageService.DeletePackageAsync(name, version);
		if (!result)
		{
			return Results.NotFound();
		}
		
		logger.LogInformation("Package deleted: {Name}@{Version} by {User}", name, version, authResult.User);
		return Results.Ok(new { message = "Package deleted successfully" });
	}
	catch (Exception ex)
	{
		logger.LogError(ex, "Error deleting package {Name}@{Version}", name, version);
		return Results.Problem("Failed to delete package");
	}
});

// Package statistics
api.MapGet("/packages/{name}/stats", async (
	IPackageService packageService,
	IValidationService validation,
	string name) =>
{
	if (!validation.IsValidPackageName(name))
	{
		return Results.BadRequest("Invalid package name");
	}
	
	var stats = await packageService.GetPackageStatsAsync(name);
	return stats != null ? Results.Ok(stats) : Results.NotFound();
});

app.Run();

// Configuration classes
class PackageManagerConfig
{
	public string PackageStoragePath { get; set; } = "Packages";
	public long MaxPackageSize { get; set; } = 25_000_000; // 25MB
	public string[] AllowedOrigins { get; set; } = ["http://localhost:3000"];
	public RateLimitConfig RateLimit { get; set; } = new();
	public string ApiKeyHeaderName { get; set; } = "X-API-Key";
	public string[] ValidApiKeys { get; set; } = [];
}

class RateLimitConfig
{
	public int RequestsPerMinute { get; set; } = 100;
	public int UploadsPerHour { get; set; } = 10;
}

class MongoDbConfig
{
	public string ConnectionString { get; set; } = "mongodb://localhost:27017";
	public string DatabaseName { get; set; } = "wpm";
}

// Domain models
class PackageMetadata
{
	public ObjectId Id { get; set; }
	public string Name { get; set; } = string.Empty;
	public string Version { get; set; } = string.Empty;
	public string Description { get; set; } = string.Empty;
	public string[] Tags { get; set; } = [];
	public string Author { get; set; } = string.Empty;
	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
	public long DownloadCount { get; set; }
	public string Checksum { get; set; } = string.Empty;
	public long FileSize { get; set; }
}

class PackageMetadataDTO
{
	public string Name { get; set; } = string.Empty;
	public string Version { get; set; } = string.Empty;
	public string Description { get; set; } = string.Empty;
	public string[] Tags { get; set; } = [];
	public string Author { get; set; } = string.Empty;
	public DateTime CreatedAt { get; set; }
	public long DownloadCount { get; set; }
	public long FileSize { get; set; }
}

class PackageUploadRequest
{
	public string Name { get; set; } = string.Empty;
	public string Version { get; set; } = string.Empty;
	public string Description { get; set; } = string.Empty;
	public string[] Tags { get; set; } = [];
	public IFormFile? File { get; set; }
	public string Author { get; set; } = string.Empty;
}

class PackageListResult
{
	public List<PackageMetadataDTO> Packages { get; set; } = [];
	public int TotalCount { get; set; }
	public int Page { get; set; }
	public int PageSize { get; set; }
}

class DownloadResult
{
	public Stream Stream { get; set; } = Stream.Null;
	public string FileName { get; set; } = string.Empty;
}

class ValidationResult
{
	public bool IsValid { get; set; }
	public List<string> Errors { get; set; } = [];
}

class UploadResult
{
	public bool IsSuccess { get; set; }
	public string Error { get; set; } = string.Empty;
	public string PackageId { get; set; } = string.Empty;
}

class AuthenticationResult
{
	public bool IsSuccess { get; set; }
	public string User { get; set; } = string.Empty;
}

class PackageStats
{
	public string Name { get; set; } = string.Empty;
	public long TotalDownloads { get; set; }
	public int VersionCount { get; set; }
	public DateTime LastUpdated { get; set; }
	public Dictionary<string, long> VersionDownloads { get; set; } = [];
}
