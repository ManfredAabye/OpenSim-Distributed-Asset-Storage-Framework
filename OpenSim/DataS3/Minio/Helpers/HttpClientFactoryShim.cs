namespace System.Net.Http;

// OpenSim prebuild projects do not include Microsoft.Extensions.Http by default.
// MinIO only needs this simple contract.
public interface IHttpClientFactory
{
    HttpClient CreateClient(string name);
}
