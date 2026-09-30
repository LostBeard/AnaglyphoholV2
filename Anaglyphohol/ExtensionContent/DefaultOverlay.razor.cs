using Anaglyphohol.Background;
using Anaglyphohol.Layout;
using Anaglyphohol.Services;
using Microsoft.AspNetCore.Components;
using SpawnDev.AccountsShared.Services;
using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.BrowserExtension;
using SpawnDev.BlazorJS.BrowserExtension.Services;
using SpawnDev.BlazorJS.TransformersJS.DepthAnythingV2;

namespace Anaglyphohol.ExtensionContent
{
    public partial class DefaultOverlay : IDisposable
    {
        [Inject]
        ContentOverlayService ContentOverlayService { get; set; } = default!;

        [Inject]
        BlazorJSRuntime JS { get; set; } = default!;

        [Inject]
        BrowserExtensionService BrowserExtensionService { get; set; } = default!;

        [Inject]
        DepthAnythingService DepthEstimationService { get; set; } = default!;

        [Inject]
        TrackedMedia TrackedMedia { get; set; } = default!;

        [Inject]
        NavigationManager NavigationManager { get; set; } = default!;

        [Inject]
        BackgroundService BackgroundService { get; set; } = default!;

        [Inject]
        AppIdentityService AppIdentityService { get; set; } = default!;

        StorageArea? SyncStorage { get; set; }
        bool beenInit = false;
        bool initComplete = false;

        /// <summary>
        /// The anaglyph profile used
        /// </summary>
        int AnaglyphProfile { get; set; }
        string AnaglyphProfileKey = nameof(AnaglyphProfile);
        /// <summary>
        /// If true, anaglyph is globally enabled. Anaglyph will be enabled on all sites where anaglyph has been previously enabled.<br/>
        /// If false, anaglyph will be disabled on all sites.
        /// </summary>
        int AnaglyphEnabledGlobal { get; set; }
        string AnaglyphEnabledGlobalKey = nameof(AnaglyphEnabledGlobal);
        ///// <summary>
        ///// If true, anaglyph images will be enabled on this site when AnaglyphEnabledGlobal is also true
        ///// </summary>
        int AnaglyphImagesEnabledSite { get; set; }
        string AnaglyphImagesEnabledSiteKey = "";
        ///// <summary>
        ///// If true, anaglyph videos will be enabled on this site when AnaglyphEnabledGlobal is also true
        ///// </summary>
        int AnaglyphVideosEnabledSite { get; set; }
        string AnaglyphVideosEnabledSiteKey = "";

        protected override async Task OnInitializedAsync()
        {
            if (beenInit) return;
            beenInit = true;
            BackgroundService.OnStateHasChanged += BackgroundService_OnStateHasChanged;
            var host = new Uri(NavigationManager.BaseUri).Host.Replace(".", "_");
            //JS.Log("Host ->", host);
            SyncStorage = BrowserExtensionService.Browser!.Storage!.Sync;
            // create host specific keys
            AnaglyphImagesEnabledSiteKey = $"{host}_{nameof(AnaglyphImagesEnabledSiteKey)}";
            AnaglyphVideosEnabledSiteKey = $"{host}_{nameof(AnaglyphVideosEnabledSiteKey)}";
            // anaglyph profile
            AnaglyphProfile = await SyncStorage.Get<int>(AnaglyphProfileKey);
            // global enabled
            AnaglyphEnabledGlobal = await SyncStorage.Get<int>(AnaglyphEnabledGlobalKey);
            // site enabled
            AnaglyphImagesEnabledSite = await SyncStorage.Get<int>(AnaglyphImagesEnabledSiteKey);
            AnaglyphVideosEnabledSite = await SyncStorage.Get<int>(AnaglyphVideosEnabledSiteKey);
            //
            DepthEstimationService.OnStateChange += DepthEstimationService_OnStateChange;

            TrackedMedia.AnaglyphImagesEnabled = AnaglyphImagesEnabled;
            TrackedMedia.AnaglyphVideosEnabled = AnaglyphVideosEnabled;
            TrackedMedia.Mode3D = AnaglyphProfile;
            TrackedMedia.OnStateChanged += ImageTracker_OnStateChanged;
            TrackedMedia.Start();

            initComplete = true;
            //ContentOverlayService.ContentOverlay.SetLoadingComplete();
            UpdateContentProgress();
            StateHasChanged();
        }
        private void BackgroundService_OnStateHasChanged()
        {
            StateHasChanged();
        }
        private void DepthEstimationService_OnStateChange()
        {
            UpdateContentProgress();
        }
        void UpdateContentProgress()
        {
            if (DepthEstimationService.Loading)
            {
                // Console.WriteLine("UpdateContentProgress 0");
                ContentOverlayService.ContentOverlay.SetLoading(DepthEstimationService.OverallLoadProgress);
            }
            else if (TrackedMedia.IsBusy)
            {
                /// Console.WriteLine("UpdateContentProgress 1");
                ContentOverlayService.ContentOverlay.SetLoading(TrackedMedia.Progress);
            }
            else
            {
                //Console.WriteLine("UpdateContentProgress 2");
                ContentOverlayService.ContentOverlay.SetLoadingComplete();
            }
        }
        private void ImageTracker_OnStateChanged()
        {
            UpdateContentProgress();
            StateHasChanged();
        }
        bool AnaglyphImagesEnabled => AnaglyphEnabledGlobal == 1 && AnaglyphImagesEnabledSite == 1;
        bool AnaglyphVideosEnabled => AnaglyphEnabledGlobal == 1 && AnaglyphVideosEnabledSite == 1;
        async Task AnaglyphEnabledGlobal_OnClicked(int index)
        {
            AnaglyphEnabledGlobal = index;
            if (SyncStorage != null) await SyncStorage.Set(AnaglyphEnabledGlobalKey, AnaglyphEnabledGlobal);
            // handle change
            TrackedMedia.AnaglyphImagesEnabled = AnaglyphImagesEnabled;
            TrackedMedia.AnaglyphVideosEnabled = AnaglyphVideosEnabled;
            StateHasChanged();
        }
        async Task SetLevel3D(double value)
        {
            //if (SyncStorage != null) await SyncStorage.Set(nameof(SetLevel3D), value);
            // handle change
            TrackedMedia.Level3D = (float)value;
        }
        async Task SetFocus3D(double value)
        {
            //if (SyncStorage != null) await SyncStorage.Set(nameof(SetFocus3D), value);
            // handle change
            TrackedMedia.Focus3D = (float)value;
        }
        async Task AnaglyphVideosEnabledSite_OnClicked(int index)
        {
            AnaglyphVideosEnabledSite = index;
            if (SyncStorage != null) await SyncStorage.Set(AnaglyphVideosEnabledSiteKey, AnaglyphVideosEnabledSite);
            // handle change
            TrackedMedia.AnaglyphVideosEnabled = AnaglyphVideosEnabled;
            StateHasChanged();
        }
        void GotoSite(string url, bool newWindow)
        {
            if (newWindow)
            {
                using var window = JS.Get<SpawnDev.BlazorJS.JSObjects.Window>("window");
                using var newWin = window.Open(url, "_blank");
                newWin?.Focus();
                StateHasChanged();
            }
            else
            {
                NavigationManager.NavigateTo(url);
            }
        }
        void GotoSite()
        {
            using var window = JS.Get<SpawnDev.BlazorJS.JSObjects.Window>("window");
            using var newWin = window.Open("https://www.spawndev.com/About", "_blank");
            newWin?.Focus();
            StateHasChanged();
        }
        async Task AnaglyphImagesEnabledSite_OnClicked(int index)
        {
            AnaglyphImagesEnabledSite = index;
            if (SyncStorage != null) await SyncStorage.Set(AnaglyphImagesEnabledSiteKey, AnaglyphImagesEnabledSite);
            // handle change
            TrackedMedia.AnaglyphImagesEnabled = AnaglyphImagesEnabled;
            StateHasChanged();
        }
        async Task AnaglyphProfile_OnClicked(int index)
        {
            AnaglyphProfile = index;
            //Console.WriteLine($"AnaglyphProfile: {AnaglyphProfile}");
            if (SyncStorage != null) await SyncStorage.Set(AnaglyphProfileKey, AnaglyphProfile);
            // handle change
            TrackedMedia.Mode3D = AnaglyphProfile;
            StateHasChanged();
        }
        public void Dispose()
        {
            Console.WriteLine($"{GetType().Name}.Dispose");
            DepthEstimationService.OnStateChange -= DepthEstimationService_OnStateChange;
            TrackedMedia.OnStateChanged -= ImageTracker_OnStateChanged;
            TrackedMedia.Dispose();
        }
    }
}
