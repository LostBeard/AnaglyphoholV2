using Anaglyphohol.Layout;
using Anaglyphohol.Services;
using Anaglyphohol.Services.Gpu;
using Microsoft.AspNetCore.Components;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.BrowserExtension;
using SpawnDev.SpawnJS.BrowserExtension.Services;

namespace Anaglyphohol.ExtensionContent
{
    public partial class DefaultOverlay : IDisposable
    {
        [Inject]
        ContentOverlayService ContentOverlayService { get; set; } = default!;

        [Inject]
        SpawnJSRuntime JS { get; set; } = default!;

        [Inject]
        BrowserExtensionService BrowserExtensionService { get; set; } = default!;

        [Inject]
        DepthService DepthService { get; set; } = default!;

        [Inject]
        TrackedMedia TrackedMedia { get; set; } = default!;


        StorageArea? SyncStorage { get; set; }
        bool beenInit = false;
        bool initComplete = false;

        /// <summary>
        /// The 3D mode (<see cref="ThreeDMode"/>): 0 red/cyan, 1 green/magenta, 2 Dimenco 2D+Z
        /// </summary>
        int AnaglyphProfile { get; set; }
        string AnaglyphProfileKey = nameof(AnaglyphProfile);
        /// <summary>
        /// If 1, 3D is globally enabled: enabled on every site where it has been enabled.<br/>
        /// If 0, disabled on all sites.
        /// </summary>
        int AnaglyphEnabledGlobal { get; set; }
        string AnaglyphEnabledGlobalKey = nameof(AnaglyphEnabledGlobal);
        /// <summary>
        /// If 1, 3D images are enabled on this site (when AnaglyphEnabledGlobal is also 1)
        /// </summary>
        int AnaglyphImagesEnabledSite { get; set; }
        string AnaglyphImagesEnabledSiteKey = "";
        /// <summary>
        /// If 1, 3D videos are enabled on this site (when AnaglyphEnabledGlobal is also 1)
        /// </summary>
        int AnaglyphVideosEnabledSite { get; set; }
        string AnaglyphVideosEnabledSiteKey = "";

        protected override async Task OnInitializedAsync()
        {
            if (beenInit) return;
            beenInit = true;
            // Per-SITE keys use the page host. The BlazorJS build used NavigationManager.BaseUri, which in a content
            // script was the extension URL (content.js set blazorBaseURI), so those "site" keys were really global.
            var host = BrowserExtensionService.LocationUri.Host.Replace(".", "_");
            SyncStorage = BrowserExtensionService.Browser!.Storage!.Sync;
            AnaglyphImagesEnabledSiteKey = $"{host}_{nameof(AnaglyphImagesEnabledSiteKey)}";
            AnaglyphVideosEnabledSiteKey = $"{host}_{nameof(AnaglyphVideosEnabledSiteKey)}";
            AnaglyphProfile = await SyncStorage.Get<int>(AnaglyphProfileKey, 0);
            // Defaults match the store version (vjs/anglyphoholv3): 3D works out of the box on the recommended sites (the
            // sites TJ tested) and is one click away anywhere else. Before, everything defaulted to off, so a fresh install
            // did nothing until the user found three toggles.
            AnaglyphEnabledGlobal = await SyncStorage.Get<int>(AnaglyphEnabledGlobalKey, 1);
            int siteDefault = TrackedMedia.IsRecommendedHost(BrowserExtensionService.LocationUri.Host) ? 1 : 0;
            AnaglyphImagesEnabledSite = await SyncStorage.Get<int>(AnaglyphImagesEnabledSiteKey, siteDefault);
            AnaglyphVideosEnabledSite = await SyncStorage.Get<int>(AnaglyphVideosEnabledSiteKey, siteDefault);

            DepthService.OnStateChange += DepthService_OnStateChange;

            TrackedMedia.AnaglyphImagesEnabled = AnaglyphImagesEnabled;
            TrackedMedia.AnaglyphVideosEnabled = AnaglyphVideosEnabled;
            TrackedMedia.Mode3D = (ThreeDMode)AnaglyphProfile;
            TrackedMedia.OnStateChanged += TrackedMedia_OnStateChanged;
            TrackedMedia.Start();

            initComplete = true;
            UpdateContentProgress();
            StateHasChanged();
        }
        private void DepthService_OnStateChange()
        {
            UpdateContentProgress();
        }
        void UpdateContentProgress()
        {
            var overlay = ContentOverlayService.ContentOverlay;
            if (overlay == null) return;
            if (DepthService.Loading)
            {
                overlay.SetLoading(DepthService.LoadProgress);
            }
            else if (TrackedMedia.IsBusy)
            {
                overlay.SetLoading(TrackedMedia.Progress);
            }
            else
            {
                overlay.SetLoadingComplete();
            }
        }
        private void TrackedMedia_OnStateChanged()
        {
            UpdateContentProgress();
            _ = InvokeAsync(StateHasChanged);
        }
        bool AnaglyphImagesEnabled => AnaglyphEnabledGlobal == 1 && AnaglyphImagesEnabledSite == 1;
        bool AnaglyphVideosEnabled => AnaglyphEnabledGlobal == 1 && AnaglyphVideosEnabledSite == 1;
        async Task AnaglyphEnabledGlobal_OnClicked(int index)
        {
            AnaglyphEnabledGlobal = index;
            if (SyncStorage != null) await SyncStorage.Set(AnaglyphEnabledGlobalKey, AnaglyphEnabledGlobal);
            TrackedMedia.AnaglyphImagesEnabled = AnaglyphImagesEnabled;
            TrackedMedia.AnaglyphVideosEnabled = AnaglyphVideosEnabled;
            StateHasChanged();
        }
        Task SetLevel3D(double value)
        {
            TrackedMedia.Level3D = (float)value;
            return Task.CompletedTask;
        }
        Task SetFocus3D(double value)
        {
            TrackedMedia.Focus3D = (float)value;
            return Task.CompletedTask;
        }
        async Task AnaglyphVideosEnabledSite_OnClicked(int index)
        {
            AnaglyphVideosEnabledSite = index;
            if (SyncStorage != null) await SyncStorage.Set(AnaglyphVideosEnabledSiteKey, AnaglyphVideosEnabledSite);
            TrackedMedia.AnaglyphVideosEnabled = AnaglyphVideosEnabled;
            StateHasChanged();
        }
        async Task AnaglyphImagesEnabledSite_OnClicked(int index)
        {
            AnaglyphImagesEnabledSite = index;
            if (SyncStorage != null) await SyncStorage.Set(AnaglyphImagesEnabledSiteKey, AnaglyphImagesEnabledSite);
            TrackedMedia.AnaglyphImagesEnabled = AnaglyphImagesEnabled;
            StateHasChanged();
        }
        async Task AnaglyphProfile_OnClicked(int index)
        {
            AnaglyphProfile = index;
            if (SyncStorage != null) await SyncStorage.Set(AnaglyphProfileKey, AnaglyphProfile);
            TrackedMedia.Mode3D = (ThreeDMode)AnaglyphProfile;
            StateHasChanged();
        }
        public void Dispose()
        {
            DepthService.OnStateChange -= DepthService_OnStateChange;
            TrackedMedia.OnStateChanged -= TrackedMedia_OnStateChanged;
            TrackedMedia.Dispose();
        }
    }
}
