﻿using Azure.Identity;
using Azure.Storage.Blobs;
using NAudio.Wave;
using RecordingStreaming.Interfaces;

namespace RecordingStreaming.Services
{
    public class BlobStorageService : IStorageService
    {
        private readonly ILogger<BlobStorageService> _logger;
        private readonly BlobContainerClient _blobContainerClient;

        public BlobStorageService(IConfiguration configuration, ILogger<BlobStorageService> logger)
        {
            _logger = logger;
            _blobContainerClient = new BlobContainerClient(new Uri(configuration["StorageContainerUri"]),
                new ManagedIdentityCredential());
            _blobContainerClient.CreateIfNotExistsAsync();
        }

        public async Task<Uri> StreamTo(Stream stream, string? fileName = null)
        {
            var blobClient = _blobContainerClient.GetBlobClient(fileName ?? new Guid().ToString());
            
            stream.Seek(0, SeekOrigin.Begin);
            var wavStream = new RawSourceWaveStream(stream, new WaveFormat(16000, 1));
            WaveFileWriter.CreateWaveFile($"recordings/{fileName}", wavStream);

            if (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development")
            {
                _logger.LogInformation("Blob upload is disabled in development environment.");
                return new Uri($"recordings/{fileName}");
            }

            await blobClient.UploadAsync($"recordings/{fileName}", true);
            _logger.LogInformation($"Audio data received for {fileName} at {blobClient.Uri}");
            
            return blobClient.Uri;
        }

        public async Task<Uri> UploadTo(string sourceFilePath, string? fileName = null)
        {
            var blobClient = _blobContainerClient.GetBlobClient(fileName ?? sourceFilePath);

            if (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development")
            {
                _logger.LogInformation("Blob upload is disabled in development environment.");
                return new Uri($"recordings/{fileName}");
            }

            await using var outputBlob = await blobClient.OpenWriteAsync(true);
            var bytes = await File.ReadAllBytesAsync(sourceFilePath);
            await outputBlob.WriteAsync(bytes);

            _logger.LogInformation($"{fileName} uploaded to {blobClient.Uri}.  Wrote {bytes.Length} bytes.");
            return blobClient.Uri;
        }

        public async Task<string> DownloadTo(string downloadUri, string? fileName = null)
        {
            if (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development")
            {
                _logger.LogInformation("Blob download is disabled in development environment.");
                return downloadUri;
            }

            var blobFileName = downloadUri.Split("/").LastOrDefault();
            var blobClient = _blobContainerClient.GetBlobClient(blobFileName);
            var downloadFileName = $"recordings/download-{fileName ?? blobFileName}";
            await using var fileStream = File.Create(downloadFileName);
            using var readBlob = await blobClient.DownloadToAsync(fileStream);

            _logger.LogInformation($"Recording {blobFileName} downloaded to {downloadFileName}.");
            return downloadFileName;
        }

        public async Task<(bool, Uri)> Exists(string fileName)
        {
            var blobClient = _blobContainerClient.GetBlobClient(fileName);
            return (await blobClient.ExistsAsync(), blobClient.Uri);
        }
    }
}
