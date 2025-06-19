using Anaglyphohol.Services;
using Bink;
using Bink.Encryption.Asymmetric.ChaosNacl;
using Bink.Signing;
using Bink.Signing.ChaosNacl;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.BrowserExtension;
using System.Security.Claims;

namespace SpawnDev.AccountsShared.Services
{
    public class AppIdentityService : AuthenticationStateProvider, IAsyncBackgroundService
    {
        public event Func<AppNeedsRestartToLoadUserArgs, Task> AppNeedsRestartToLoadUser;
        public delegate void AuthenticationStateChangeCompleteDelegate(ClaimsPrincipal? userOld, ClaimsPrincipal user);
        public event AuthenticationStateChangeCompleteDelegate AuthenticationStateChangeComplete;
        public bool RestartToLoadUser { get; private set; }
        public string Token { get; private set; } = "";
        public bool TokenHasBeenSet { get; private set; }
        public ClaimsPrincipal User { get; private set; } = _anonymous;
        private AuthenticationState AuthenticationState { get; set; } = new AuthenticationState(_anonymous);
        private static ClaimsPrincipal _anonymous { get; } = new ClaimsPrincipal(new ClaimsIdentity());
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(AuthenticationState);
        public AsymKeyPair? InstallKeys { get; private set; } = null;
        public AsymKeyPair? InstanceKeys { get; private set; } = null;
        public string InstancePublicKey => InstanceKeys!.PublicKey;
        public string InstallPublicKey => InstallKeys!.PublicKey;
        public string DisplayUsername => User.Username();
        public string DisplayUsernameCharacter => User.UsernameCharacter();
        public string DisplayUsernameDeviceName => $"{DisplayUsername}@{DeviceName}";
        public string DisplayUsernameDeviceNameInstanceKeyStub => $"{DisplayUsername}@{DeviceName}.{InstancePublicKey.Substring(0, 16)}";
        public string DisplayUsernameDeviceNameDomain => $"{DisplayUsername}@{DeviceName}.{HostDomain}";
        public string AppId { get; private set; } = "";
        public string DeviceName { get; private set; } = "";
        public ChaosNaclSigner Signer { get; private set; } = new ChaosNaclSigner();
        public ChaosNaclAsymmetricEncryption Encrypter { get; private set; } = new ChaosNaclAsymmetricEncryption();
        BlazorJSRuntime JS { get; }
        NavigationManager navigationManager;
        public string BaseAddress { get; }
        public string HostDomain { get; }
        public string UserAgent { get; } = "";
        public Task Ready => _Ready ??= InitAsync();
        private Task? _Ready = null;
        SyncStorageService DefaultCache;
        public AppIdentityService(BlazorJSRuntime js, NavigationManager navigationManager, SyncStorageService syncStorageService)
        {
            DefaultCache = syncStorageService;
            JS = js;
            this.navigationManager = navigationManager;
            AppId = AppDomain.CurrentDomain.FriendlyName;
            BaseAddress = navigationManager.BaseUri;
            HostDomain = new Uri(BaseAddress).Host;
            try
            {
                UserAgent = JS.Get<string>("navigator.userAgent");
            }
            catch { }
            if (DefaultCache.SyncStorage != null)
            {
                DefaultCache.SyncStorage.OnChanged += SyncStorage_OnChanged;
            }
        }
        void SyncStorage_OnChanged(StorageChanges changes, string value)
        {
            var keys = changes.Keys;
            var tokenChanged = keys.Contains(TokenKey);
            if (tokenChanged)
            {
                _ = UpdateTokenFromStorage();
            }
        }
        async Task UpdateTokenFromStorage()
        {
            try
            {
                var token = await ReadToken();
                await SetToken(token);
            }
            catch { }
        }
        static string AuthenticationTokenPath = $"/.etc/AuthenticationToken";
        public Task Logout() => SetToken("");
        public void GoHome(bool forceReload = false)
        {
            navigationManager.NavigateTo("", forceReload);
        }
        public void ReloadPage(bool forceReload = false)
        {
            navigationManager.NavigateTo(navigationManager.Uri, forceReload);
        }
        async Task<string> ReadToken()
        {
            return await DefaultCache.ReadText(AuthenticationTokenPath) ?? "";
        }
        async Task WriteToken(string token)
        {
            await DefaultCache.WriteText(AuthenticationTokenPath, token);
        }
        public byte[] AsymEncryptInstall(byte[] encryptedMessage, string recipientPublicKey)
        {
            if (InstanceKeys == null) throw new NullReferenceException(nameof(InstallKeys));
            return Encrypter.Encrypt(encryptedMessage, recipientPublicKey, InstallKeys.PrivateKey);
        }
        public byte[] AsymDecryptInstall(byte[] encryptedMessage, string senderPublicKey)
        {
            if (InstanceKeys == null) throw new NullReferenceException(nameof(InstallKeys));
            return Encrypter.Decrypt(encryptedMessage, InstallKeys.PrivateKey, senderPublicKey);
        }
        public byte[] AsymEncryptInstance(byte[] encryptedMessage, string recipientPublicKey)
        {
            if (InstanceKeys == null) throw new NullReferenceException(nameof(InstanceKeys));
            return Encrypter.Encrypt(encryptedMessage, recipientPublicKey, InstanceKeys.PrivateKey);
        }
        public byte[] AsymDecryptInstance(byte[] encryptedMessage, string senderPublicKey)
        {
            if (InstanceKeys == null) throw new NullReferenceException(nameof(InstanceKeys));
            return Encrypter.Decrypt(encryptedMessage, InstanceKeys.PrivateKey, senderPublicKey);
        }
        public void Sign<T>(T obj, TimeSpan timeToLive, bool clear = true) where T : IMultiSignedObject
        {
            if (clear) obj.Signatures.Clear();
            Signer.MultiSign(obj, new[] { InstallKeys, InstanceKeys }, timeToLive, new[] { "install", "instance" });
        }
        public void Sign<T>(T obj, bool clear = true) where T : IMultiSignedObject
        {
            if (clear) obj.Signatures.Clear();
            Signer.MultiSign(obj, new[] { InstallKeys, InstanceKeys }, new[] { "install", "instance" });
        }
        public bool Verify<T>(T obj, string instanceId, TimeSpan? maxSignTimeDeviation = null, bool verifyTimestampIfExpirable = true) where T : IMultiSignedObject
        {
            var verified = Signer.MultiVerify(obj, null, maxSignTimeDeviation, verifyTimestampIfExpirable);
            if (!verified) return false;
            if (!GetKeyPres(instanceId, out var installPublicKeyPre, out var instancePublicKeyPre)) return false;
            var installSig = obj.Signatures.Where(o => o.PublicKey.StartsWith(installPublicKeyPre)).FirstOrDefault();
            var instanceSig = obj.Signatures.Where(o => o.PublicKey.StartsWith(instancePublicKeyPre)).FirstOrDefault();
            return installSig != null && instanceSig != null;
        }
        // separates the instanceId into 2 parts
        // parts[0] = install if 
        public bool GetKeyPres(string instanceId, out string installPublicKeyPre, out string instancePublicKeyPre)
        {
            if (!string.IsNullOrEmpty(instanceId) || instanceId.Length != 32)
            {
                var splitPos = instanceId.Length / 2;
                installPublicKeyPre = instanceId.Substring(0, splitPos);
                instancePublicKeyPre = instanceId.Substring(splitPos);
                return true;
            }
            else
            {
                installPublicKeyPre = "";
                instancePublicKeyPre = "";
                return false;
            }
        }
        public async Task<AsymKeyPair> KeyPairCreate(string keyName, bool saveToStore = true)
        {
            var keyPair = Signer.KeyPairCreate(keyName);
            if (saveToStore) await WriteKey(keyPair);
            return keyPair;
        }
        public Task<AsymKeyPair> InstallKeysPairCreate(bool saveToStore = true) => KeyPairCreate(nameof(InstallKeys), saveToStore);
        public async Task WipeUserData()
        {
            // unsets the set token and the publickey associated with it
            await SetToken("");
            // app should restart
        }
        string TokenKey = "";
        const string KeyFile = nameof(KeyFile);
        private async Task InitAsync()
        {
            await DefaultCache.Ready;
            TokenKey = await DefaultCache.GetEKey(AuthenticationTokenPath);
            var installKeys = await ReadKey();
            if (installKeys == null)
            {
                //JS.Log("Creating installKeys...");
                installKeys = await InstallKeysPairCreate();
                //JS.Log("Created installKeys.");
            }
            InstallKeys = installKeys;
            // load token (if one)
            // Token, User, and InstallKeys
            var token = await ReadToken();
            await SetToken(token);
            //JS.Log($"AppId: {AppId}");
            //JS.Log($"AppIdentityService UserId: [{User.Username()}] [{User.UserId()}]");
            //JS.Log($"Install public key: {InstallKeys!.PublicKey}");
            //JS.Log($"Instance public key: {InstanceKeys!.PublicKey}");
            //JS.Log($"DeviceName: {DeviceName}");
        }
        public async Task WriteKey(AsymKeyPair asymKeyPair)
        {
            await DefaultCache.WriteJSON(KeyFile, asymKeyPair);
        }
        public async Task<bool> DeleteKey()
        {
            return await DefaultCache.Delete(KeyFile);
        }
        async Task<AsymKeyPair?> ReadKey()
        {
            return await DefaultCache.ReadJSON<AsymKeyPair>(KeyFile);
        }
        public async Task SetToken(string token)
        {
            try
            {
                var firstTokenSet = !TokenHasBeenSet;
                if (!firstTokenSet && Token == token) return;
                var rolesOld = User.Roles();
                TokenHasBeenSet = true;
                var userOld = firstTokenSet ? null : User;
                var userIdActive = userOld?.UserId();
                var userNameActive = userOld?.Username();
                ClaimsPrincipal tokenUser;
                try
                {
                    tokenUser = JwtTokenReader.GetClaimsPrincipal(token, false);
                }
                catch
                {
                    token = "";
                    tokenUser = JwtTokenReader.Anonymous;
                }
                if (!firstTokenSet)
                {
                    var readBack = await ReadToken();
                    if (token != readBack)
                    {
                        await WriteToken(token);
                    }
                }
                var userNameToken = tokenUser.Username();
                var userChanged = !firstTokenSet && userNameActive != userNameToken;
                if (!userChanged)
                {
                    User = tokenUser;
                    DeviceName = !string.IsNullOrEmpty(User.DeviceName()) ? User.DeviceName() : InstallKeys!.PublicKey.Substring(0, 20);
                    if (InstanceKeys == null) InstanceKeys = Signer.KeyPairCreate(nameof(InstanceKeys));
                    Token = User.LoggedIn() ? token : "";
                    AuthenticationState = new AuthenticationState(User.LoggedIn() ? tokenUser : _anonymous);
                    NotifyAuthenticationStateChanged(Task.FromResult(AuthenticationState));
                    AuthenticationStateChangeComplete?.Invoke(userOld, User);
                }
                else
                {
                    User = tokenUser;
                    DeviceName = !string.IsNullOrEmpty(User.DeviceName()) ? User.DeviceName() : InstallKeys!.PublicKey.Substring(0, 20);
                    if (InstanceKeys == null) InstanceKeys = Signer.KeyPairCreate(nameof(InstanceKeys));
                    Token = User.LoggedIn() ? token : "";
                    AuthenticationState = new AuthenticationState(User.LoggedIn() ? tokenUser : _anonymous);
                    NotifyAuthenticationStateChanged(Task.FromResult(AuthenticationState));
                    AuthenticationStateChangeComplete?.Invoke(userOld, User);
                }
            }
            catch (Exception ex)
            {
                JS.Log($"SetToken failed: {ex.Message}");
                JS.Log($"StackTrace: {ex.StackTrace}");
            }
            finally
            {
                //JS.Log("<< SetToken");
            }
        }
    }
    public class AppNeedsRestartToLoadUserArgs
    {
        public ClaimsPrincipal User { get; set; }
        public bool RestartNow { get; set; } = true;
    }
}
