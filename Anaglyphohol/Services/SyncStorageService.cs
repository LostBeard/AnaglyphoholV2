using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.BrowserExtension;
using SpawnDev.BlazorJS.BrowserExtension.Services;
using SpawnDev.BlazorJS.Cryptography;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Anaglyphohol.Services
{
    public class SyncStorageService : IAsyncBackgroundService
    {
        Task? _Ready = null;
        public Task Ready => _Ready ??= InitAsync();
        public StorageArea? SyncStorage { get; private set; }
        BrowserExtensionService BrowserExtensionService;
        BrowserWASMCrypto BrowserCrypto;
        PortableAESGCMKey? gcm = null;
        public bool Supported => SyncStorage != null && gcm != null;
        BlazorJSRuntime JS;
        public SyncStorageService(BrowserExtensionService browserExtensionService, BrowserWASMCrypto browserCrypto, BlazorJSRuntime js)
        {
            JS = js;
            BrowserCrypto = browserCrypto;
            BrowserExtensionService = browserExtensionService;
            SyncStorage = BrowserExtensionService.Browser?.Storage?.Sync;
        }
        byte[] existingKey = new byte[0];
        public string QueryableKey { get; private set; }
        string Id = Guid.NewGuid().ToString();
        static string StaticId = Guid.NewGuid().ToString();
        async Task InitAsync()
        {
            JS.Log("InitAsync", StaticId, Id);
            if (BrowserExtensionService.ExtensionMode != ExtensionMode.None && SyncStorage != null)
            {
                // 
                try
                {
                    var existingKeyB64 = await SyncStorage!.Get<string?>("DAAAAHyPdmVZKe7nwHkwLxAAAAAnTXAIpmCDXeDuPJkUyHBcf/82Np4an3cRB5VY3XVw2v1FL4HzFAy5U4L4IgGRaJmYLmgDOyPQXBN/Wp9JsJ+2Aw/vXOry");
                    if (string.IsNullOrEmpty(existingKeyB64))
                    {
                        existingKey = RandomNumberGenerator.GetBytes(64);
                        existingKeyB64 = Convert.ToBase64String(existingKey);
                        await SyncStorage.Set("DAAAAHyPdmVZKe7nwHkwLxAAAAAnTXAIpmCDXeDuPJkUyHBcf/82Np4an3cRB5VY3XVw2v1FL4HzFAy5U4L4IgGRaJmYLmgDOyPQXBN/Wp9JsJ+2Aw/vXOry", existingKeyB64);
                    }
                    else
                    {
                        existingKey = Convert.FromBase64String(existingKeyB64);
                    }
                    gcm = await BrowserCrypto.GenerateAESGCMKey(existingKey);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"SyncStorageService.InitAsync failed: {ex.Message}");
                }
                if (gcm == null)
                {
                    // 
                    try
                    {
                        existingKey = RandomNumberGenerator.GetBytes(64);
                        var existingKeyB64 = Convert.ToBase64String(existingKey);
                        await SyncStorage.Set("DAAAAHyPdmVZKe7nwHkwLxAAAAAnTXAIpmCDXeDuPJkUyHBcf/82Np4an3cRB5VY3XVw2v1FL4HzFAy5U4L4IgGRaJmYLmgDOyPQXBN/Wp9JsJ+2Aw/vXOry", existingKeyB64);
                        gcm = await BrowserCrypto.GenerateAESGCMKey(existingKey);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"SyncStorageService.InitAsync failed: {ex.Message}");
                    }
                }
                QueryableKey = Convert.ToBase64String(await BrowserCrypto.Digest("SHA-512", existingKey));
                JS.Log("QueryableKey", Id, QueryableKey);
            }
        }
        static JsonSerializerOptions JsonSerializerOptionsDefault = new JsonSerializerOptions { AllowTrailingCommas = true, PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, };
        public Task WriteJSON(string key, object value) => Set(key, value);
        public async Task Set(string key, object value)
        {
            if (SyncStorage == null) return;
            var eKey = await GetEKey(key);
            var ejson = Convert.ToBase64String(await BrowserCrypto.Encrypt(gcm, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonSerializerOptionsDefault))));
            await SyncStorage!.Set(eKey, ejson);
        }
        public async Task<string> GetEKey(string key)
        {
            var eKey = Convert.ToBase64String(await BrowserCrypto.Digest("SHA-512", Encoding.UTF8.GetBytes(key).Concat(existingKey).ToArray()));
            return eKey;
        }
        public async Task WriteText(string key, string value)
        {
            if (SyncStorage == null) return;
            var eKey = await GetEKey(key);
            var ejsonBytes = await BrowserCrypto.Encrypt(gcm, Encoding.UTF8.GetBytes(value));
            var ejson = Convert.ToBase64String(ejsonBytes);
            await SyncStorage!.Set(eKey, ejson);
        }
        public async Task<bool> Exists(string key)
        {
            if (SyncStorage == null) return false;
            try
            {
                var eKey = await GetEKey(key);
                return await SyncStorage!.Exists(eKey);
            }
            catch { }
            return false;
        }
        public async Task<bool> Delete(string key)
        {
            await Remove(key);
            return true;
        }
        public async Task Remove(string key)
        {
            if (SyncStorage == null) return;
            try
            {
                var eKey = await GetEKey(key);
                await SyncStorage!.Remove(eKey);
            }
            catch { }
        }
        public async Task<string?> ReadText(string key)
        {
            if (SyncStorage == null) return default!;
            try
            {
                var eKey = await GetEKey(key);
                var ejson = await SyncStorage!.Get<string>(eKey);
                var ejsonBytes = Convert.FromBase64String(ejson);
                var jsonBytes = await BrowserCrypto.Decrypt(gcm!, ejsonBytes);
                var json = Encoding.UTF8.GetString(jsonBytes);
                return json;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SyncStorageService.Get failed: " + ex.Message);
            }
            return default!;
        }
        public Task<T> ReadJSON<T>(string key) => Get<T>(key);
        public async Task<T> Get<T>(string key)
        {
            if (SyncStorage == null) return default!;
            try
            {
                var eKey = await GetEKey(key);
                var ejsonBase64 = await SyncStorage!.Get<string>(eKey);
                var ejsonBytes = Convert.FromBase64String(ejsonBase64);
                var jsonBytes = await BrowserCrypto.Decrypt(gcm!, ejsonBytes);
                var json = Encoding.UTF8.GetString(jsonBytes);
                return JsonSerializer.Deserialize<T>(json, JsonSerializerOptionsDefault)!;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SyncStorageService.Get failed: " + ex.Message);
            }
            return default!;
        }
    }
}
