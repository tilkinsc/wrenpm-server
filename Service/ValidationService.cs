
using System.IO.Compression;
using System.Text.RegularExpressions;

interface IValidationService
{
	bool IsValidPackageName(string name);
	bool IsValidVersion(string version);
	ValidationResult ValidateUploadRequest(PackageUploadRequest request);
	Task<bool> ValidateZipFileAsync(Stream stream);
}

partial class ValidationService : IValidationService
{
	private static readonly Regex VersionRegex = CompiledVersionRegex();
	private static readonly Regex PackageNameRegex = CompiledPackageNameRegex();
	private readonly char[] _invalidChars = Path.GetInvalidFileNameChars();

	public bool IsValidPackageName(string name)
	{
		return !string.IsNullOrWhiteSpace(name) &&
			name.Length <= 100 &&
			!name.Contains("..") &&
			!name.Contains('/') &&
			!name.Contains('\\') &&
			!name.Any(c => _invalidChars.Contains(c)) &&
			PackageNameRegex.IsMatch(name);
	}

	public bool IsValidVersion(string version)
	{
		return !string.IsNullOrWhiteSpace(version) && VersionRegex.IsMatch(version);
	}

	public ValidationResult ValidateUploadRequest(PackageUploadRequest request)
	{
		var result = new ValidationResult { IsValid = true };

		if (string.IsNullOrWhiteSpace(request.Name))
			result.Errors.Add("Package name is required");
		else if (!IsValidPackageName(request.Name))
			result.Errors.Add("Invalid package name format");

		if (string.IsNullOrWhiteSpace(request.Version))
			result.Errors.Add("Version is required");
		else if (!IsValidVersion(request.Version))
			result.Errors.Add("Version must follow semantic versioning (e.g., 1.0.0)");

		if (request.Description?.Length > 1000)
			result.Errors.Add("Description too long (max 1000 characters)");

		if (request.Tags?.Length > 10)
			result.Errors.Add("Too many tags (max 10)");

		if (request.Tags?.Any(tag => tag.Length > 50) == true)
			result.Errors.Add("Tag too long (max 50 characters each)");

		if (request.File == null)
			result.Errors.Add("Package file is required");
		else if (request.File.ContentType != "application/zip" ||
				!Path.GetExtension(request.File.FileName).Equals(".zip", StringComparison.OrdinalIgnoreCase))
			result.Errors.Add("Only .zip files are allowed");

		result.IsValid = result.Errors.Count == 0;
		return result;
	}

	public async Task<bool> ValidateZipFileAsync(Stream stream)
	{
		try
		{
			using var archive = new ZipArchive(stream, ZipArchiveMode.Read, true);

			// Basic validation - check if it's a valid zip and not a zip bomb
			var totalUncompressedSize = 0L;
			var entryCount = 0;

			foreach (var entry in archive.Entries)
			{
				entryCount++;
				totalUncompressedSize += entry.Length;

				// Prevent zip bombs
				if (totalUncompressedSize > 100_000_000 || entryCount > 1000)
					return false;

				// Check for path traversal
				if (entry.FullName.Contains("..") || Path.IsPathRooted(entry.FullName))
					return false;
			}

			return true;
		}
		catch
		{
			return false;
		}
	}

	[GeneratedRegex(@"^\d+\.\d+\.\d+$", RegexOptions.Compiled)]
	private static partial Regex CompiledVersionRegex();
	[GeneratedRegex(@"^[a-zA-Z0-9_-]+$", RegexOptions.Compiled)]
	private static partial Regex CompiledPackageNameRegex();
}
