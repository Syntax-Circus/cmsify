using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Cmsify.Core.Interfaces.Services;
using Microsoft.Extensions.Configuration;

namespace Cmsify.Infrastructure.Storage;

public sealed class S3BlobStorageProvider : IStorageProvider
{
    public const string ProviderName = "s3";

    private readonly IAmazonS3 client;
    private readonly string bucketName;

    public S3BlobStorageProvider(IConfiguration configuration)
        : this(CreateClient(configuration), configuration["Storage:S3:BucketName"] ?? throw new InvalidOperationException("Storage:S3:BucketName is required."))
    {
    }

    internal S3BlobStorageProvider(IAmazonS3 client, string bucketName)
    {
        this.client = client;
        this.bucketName = bucketName;
    }

    public async Task<StoredFile> StoreAsync(Stream content, string fileName, string mimeType, CancellationToken ct = default)
    {
        var storageKey = StorageKeyBuilder.Build(fileName);
        var request = new PutObjectRequest
        {
            BucketName = bucketName,
            Key = storageKey,
            InputStream = content,
            ContentType = mimeType
        };

        await client.PutObjectAsync(request, ct);
        return new StoredFile(storageKey, ProviderName, content.CanSeek ? content.Length : 0);
    }

    public async Task<Stream> RetrieveAsync(string storageKey, CancellationToken ct = default)
    {
        var response = await client.GetObjectAsync(bucketName, storageKey, ct);
        return response.ResponseStream;
    }

    public Task DeleteAsync(string storageKey, CancellationToken ct = default) =>
        client.DeleteObjectAsync(bucketName, storageKey, ct);

    public async Task<bool> ExistsAsync(string storageKey, CancellationToken ct = default)
    {
        try
        {
            await client.GetObjectMetadataAsync(bucketName, storageKey, ct);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private static IAmazonS3 CreateClient(IConfiguration configuration)
    {
        var accessKey = configuration["Storage:S3:AccessKey"];
        var secretKey = configuration["Storage:S3:SecretKey"];
        var region = configuration["Storage:S3:Region"] ?? RegionEndpoint.USEast1.SystemName;
        var serviceUrl = configuration["Storage:S3:ServiceUrl"];

        var config = new AmazonS3Config
        {
            RegionEndpoint = RegionEndpoint.GetBySystemName(region),
            ServiceURL = serviceUrl
        };

        if (!string.IsNullOrWhiteSpace(serviceUrl))
        {
            config.ForcePathStyle = true;
        }

        return string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey)
            ? new AmazonS3Client(config)
            : new AmazonS3Client(new BasicAWSCredentials(accessKey, secretKey), config);
    }
}
