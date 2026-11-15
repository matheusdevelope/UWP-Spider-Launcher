using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;
using Windows.Storage.AccessCache;
using Windows.Storage.Pickers;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI.Xaml.Media.Imaging;
using QRCoder;
using SpiderManInstaller.SteamTransfer;

namespace SpiderManInstaller
{
    public sealed partial class MainPage : Page
    {
        readonly List<object> sources = new List<object>();
        bool reportActive;
        long importDone, importTotal;
        CancellationTokenSource cancellation;
        WifiReceiver receiver;
        bool busy;
        readonly DispatcherTimer repairTimer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(2)};
        bool checkingRepair;
        DateTime lastUpdate;
        int qrGeneration;
        int wifiGeneration;
        bool wifiComplete;
        int downloadGeneration;
        long lastProgressSequence;
        bool downloadActive, uiReady;
        List<string> imports = new List<string>();
        public MainPage()
        {
            InitializeComponent();
            InitializeSteamLanguages();
            SizeChanged+=(s,e)=>UpdateResponsiveLayout();
            InstallCard.SizeChanged+=(s,e)=>UpdateResponsiveLayout();
            if(CombinedRuntime.Enabled)RuntimePackageInfo.Text="The runtime is included in this package. Close the game before checking it or restoring startup settings. Your existing settings and saves are preserved.";
            repairTimer.Tick+=(s,e)=>ShowRuntimeSetupResult();
            Unloaded+=(s,e)=>repairTimer.Stop();
            Window.Current.VisibilityChanged+=(s,e)=>{if(!e.Visible)steamAuthDialog?.Hide();};
            object savedWorkers;
            if(ApplicationData.Current.LocalSettings.Values.TryGetValue("download-workers",out savedWorkers))
                DownloadWorkers.SelectedIndex=Convert.ToInt32(savedWorkers)==2?0:Convert.ToInt32(savedWorkers)==8?2:1;
            uiReady=true;
            Loaded += async (s, e) =>
            {
                UpdateResponsiveLayout();
                RefreshStorage();
                RefreshPreparation();
                DiagnosticLog.Line("ui_loaded version=0.6.0.58 combined="+(CombinedRuntime.Enabled?1:0)+" local="+DiagnosticLog.Folder);
                if(CombinedRuntime.Enabled)CombinedRuntime.Log("latest_launcher_ui_loaded version=0.6.0.58 play_enabled="+PlayButton.IsEnabled);
                ShowRuntimeSetupResult();
                (PlayButton.IsEnabled?PlayButton:SteamTile).Focus(FocusState.Programmatic);

            };
        }
        void UpdateResponsiveLayout()
        {
            if(ActualWidth<=0||ActualHeight<=0)return;
            bool compact=ActualHeight<620;
            double horizontal=Math.Min(64,Math.Max(24,ActualWidth*0.04));
            double vertical=Math.Min(36,Math.Max(16,ActualHeight*0.04));
            LayoutRoot.Margin=new Thickness(horizontal,vertical,horizontal,vertical);
            HeaderRow.Height=new GridLength(compact?40:44);
            FooterRow.Height=new GridLength(compact?62:80);
            InstallCard.Padding=new Thickness(compact?12:16);
            DeviceLabel.Visibility=ActualWidth<760?Visibility.Collapsed:Visibility.Visible;
            WifiDescription.Visibility=compact?Visibility.Collapsed:Visibility.Visible;
            WifiActions.Orientation=ActualWidth<700?Orientation.Vertical:Orientation.Horizontal;
            UsbActions.Orientation=ActualWidth<700?Orientation.Vertical:Orientation.Horizontal;
            StorageActions.Orientation=ActualWidth<900?Orientation.Vertical:Orientation.Horizontal;
            WifiInfo.MaxLines=compact?1:2;
            double innerHeight=Math.Max(0,InstallCard.ActualHeight-InstallCard.Padding.Top-InstallCard.Padding.Bottom);
            if(innerHeight<=0)return;
            double qrWidth=Math.Max(96,Math.Min(148,ActualWidth*0.23));
            double wifiSize=Math.Max(64,Math.Min(qrWidth,innerHeight-22));
            double steamSize=Math.Max(64,Math.Min(Math.Min(176,ActualWidth*0.25),innerHeight));
            WifiQr.Width=WifiQr.Height=wifiSize;
            SteamQr.Width=SteamQr.Height=steamSize;
        }
        void Manage_Click(object sender,RoutedEventArgs e){MainTabs.SelectedIndex=2;RefreshStorage();}
        void InstallMethod_Click(object sender,RoutedEventArgs e)
        {
            SelectInstallMethod((string)((FrameworkElement)sender).Tag);
        }
        void SelectInstallMethod(string method)
        {
            MainTabs.SelectedIndex=1;
            SteamPanel.Visibility=method=="Steam"?Visibility.Visible:Visibility.Collapsed;
            UsbPanel.Visibility=method=="Usb"?Visibility.Visible:Visibility.Collapsed;
            WifiPanel.Visibility=method=="Wifi"?Visibility.Visible:Visibility.Collapsed;
        }
        void Workers_Changed(object sender,SelectionChangedEventArgs e)
        {
            if(!uiReady)return;
            ApplicationData.Current.LocalSettings.Values["download-workers"]=SelectedWorkers();
        }
        int SelectedWorkers()=>DownloadWorkers.SelectedIndex==0?2:DownloadWorkers.SelectedIndex==2?8:4;
        void RefreshPreparation()
        {
            if(busy)return;
            try
            {
                imports=GamePreparation.Imports();ImportList.Items.Clear();
                foreach(string p in imports)ImportList.Items.Add(Path.GetFileName(p));
                ImportList.SelectedIndex=imports.Count>0?0:-1;
                bool ready=GamePreparation.Ready();PlayButton.IsEnabled=ready;PrepareButton.IsEnabled=!ready;
                PlayButton.Content="▶  Play";
                PrepareCard.Visibility=ready?Visibility.Collapsed:Visibility.Visible;
                PreparationStatus.Text=ready?"Game files prepared. Play checks game-app setup.":"Import your game from the Install tab, then choose Prepare below.";
                if(File.Exists(Path.Combine(GamePreparation.RuntimeRoot,"deletion-pending.txt")))
                {PreparationStatus.Text="Game removal is incomplete. Open Storage to finish cleanup.";PrepareButton.IsEnabled=false;PlayButton.IsEnabled=false;}
            }
            catch(Exception ex){PlayButton.IsEnabled=false;PreparationStatus.Text="Game readiness: "+ex.Message;}
        }
        internal async void ShowRuntimeSetupResult()
        {
            if(busy || checkingRepair)return;
            checkingRepair=true;
            try
            {
                var result=RuntimeInstallation.ReadResult();
                if(result==null){repairTimer.Stop();return;}
                if(result.Status=="pending")repairTimer.Start();else repairTimer.Stop();
                RefreshPreparation();
                PlayButton.IsEnabled=result.Status!="pending" && GamePreparation.Ready();
                Status.Text=PreparationStatus.Text=result.Message;
                File.WriteAllText(Path.Combine(ApplicationData.Current.LocalFolder.Path,"runtime-repair-result.txt"),DateTime.UtcNow.ToString("o")+"\n"+result.Message);
                if(result.Status=="ok" && result.Play)await PlayGame(false);
            }
            catch(Exception ex){Status.Text="Runtime setup: "+ex.Message;}
            finally{checkingRepair=false;}
        }
        async void RepairRuntime_Click(object sender,RoutedEventArgs e){await RepairRuntime();}
        void Refresh_Click(object sender,RoutedEventArgs e){RefreshPreparation();}
        void RefreshStorage()
        {
            string root=GameStorage.ChosenRoot();
            try{DestinationInfo.Text=GameStorage.StatusLine();}
            catch(Exception ex){DestinationInfo.Text="Storage unavailable: "+ex.Message;}
            try{HomeStorage.Text=(GameStorage.FreeBytes(root)/1073741824.0).ToString("F1")+" GiB free · "+GameStorage.Describe(root);}
            catch{HomeStorage.Text=GameStorage.Describe(root)+": connect the SSD or choose Internal storage.";}
        }
        void StorageRefresh_Click(object sender,RoutedEventArgs e){if(!busy)RefreshStorage();}
        void UseInternalStorage_Click(object sender,RoutedEventArgs e)
        {
            if(busy)return;
            try{GameStorage.UseInternal();RefreshStorage();Status.Text="Game storage set to internal Xbox storage. Existing SSD files were not moved.";}
            catch(Exception ex){Status.Text="Internal storage: "+ex.Message;}
        }
        async void UseExternalStorage_Click(object sender,RoutedEventArgs e)
        {
            if(busy)return;
            try
            {
                string path=await GameStorage.UseExternalDefaultAsync();
                RefreshStorage();
                Status.Text="Game storage set to "+path+" ("+GameStorage.Describe(path)+"). Put the Steam folder there or choose Use selected folder on the Install tab.";
            }
            catch(Exception ex){Status.Text="External SSD: "+ex.Message;}
        }
        async void BrowseStorage_Click(object sender,RoutedEventArgs e){await BrowseGameFolder(false);}
        async void BrowseGameFolder_Click(object sender,RoutedEventArgs e){await BrowseGameFolder(true);}
        async Task BrowseGameFolder(bool useInPlace)
        {
            if(busy)return;
            try
            {
                var picker=new FolderPicker();
                picker.SuggestedStartLocation=PickerLocationId.ComputerFolder;
                picker.FileTypeFilter.Add("*");
                var folder=await picker.PickSingleFolderAsync();
                if(folder==null){Status.Text="No folder chosen.";}
                else
                {
                    GameStorage.ApplyPickedFolder(folder);
                    RefreshStorage();
                    if(useInPlace && GameStorage.LooksLikeGame(folder.Path))await UseFolder(folder);
                    else Status.Text="Game storage set to "+folder.Path+" ("+GameStorage.Describe(folder.Path)+").";
                }
            }
            catch(Exception ex){Status.Text="Folder picker: "+ex.Message;}
        }
        async void DeleteGame_Click(object sender,RoutedEventArgs e)
        {
            if(busy || (receiver!=null && receiver.Uploading)){CleanupStatus.Text="Finish or stop the current transfer first.";return;}
            busy=true;DeleteGameButton.IsEnabled=false;PlayButton.IsEnabled=false;PrepareButton.IsEnabled=false;
            StopWifi();
            try
            {
                CleanupStatus.Text="Checking installed game files…";
                var plan=await Task.Run(()=>GamePreparation.CleanupPlan());
                var dialog=new ContentDialog {
                    Title="Delete installed game files?",
                    Content="Remove "+(plan.Bytes/1073741824.0).ToString("F2")+" GiB in "+plan.Count+" files from this launcher's prepared game?"+(plan.ArchivesLinked?"\n\nGame archives stay on the SSD; only the internal runtime link and prepared files are removed.":"")+"\n\nClose Spider-Man before continuing. This cannot be undone; import and prepare the game again to play.\n\nYour saves, runtime app, USB originals and separate import copies are kept.",
                    PrimaryButtonText="Delete game files",CloseButtonText="Keep game",DefaultButton=ContentDialogButton.Close,RequestedTheme=ElementTheme.Dark
                };
                if(await dialog.ShowAsync()!=ContentDialogResult.Primary){CleanupStatus.Text="Nothing deleted.";return;}
                CancelButton.IsEnabled=false;Progress.Value=0;Status.Text="Deleting installed game files…";
                await Task.Run(()=>plan.Delete((done,total)=> {
                    var ignored=Dispatcher.RunAsync(CoreDispatcherPriority.Low,()=>{Progress.Value=total==0?100:100.0*done/total;Status.Text="Deleting game files: "+done+" / "+total;});
                }));
                Progress.Value=100;CleanupStatus.Text="Installed game files removed. Saves and the runtime app were kept.";
                Status.Text="Game files removed. Import your copy again to reinstall.";
            }
            catch(Exception ex){CleanupStatus.Text="Cleanup stopped: "+ex.Message+" Close the game and retry. Saves were not targeted.";}
            finally{busy=false;DeleteGameButton.IsEnabled=true;CancelButton.IsEnabled=true;RefreshPreparation();RefreshStorage();}
        }
        bool experimentalVersionAcknowledged;
        async Task<bool> ConfirmExperimentalVersion(string profile)
        {
            if(profile!=GameVersionSupport.Newer||experimentalVersionAcknowledged)return true;
            var notice=new ContentDialog {
                Title="Newer game version — experimental",
                Content="Steam version 4.0630 is less tested on Xbox. It may have visual glitches, slowdowns or crashes. The Xbox memory limit still requires the existing reduced graphics settings.\n\nYou can continue using this version; your original game files are preserved.",
                PrimaryButtonText="Continue",CloseButtonText="Cancel",DefaultButton=ContentDialogButton.Close,RequestedTheme=ElementTheme.Dark
            };
            experimentalVersionAcknowledged=await notice.ShowAsync()==ContentDialogResult.Primary;
            return experimentalVersionAcknowledged;
        }
        async void Prepare_Click(object sender,RoutedEventArgs e){await PrepareSelected();}
        async Task PrepareSelected()
        {
            if(busy || (receiver!=null && receiver.Uploading))return;
            int selected=ImportList.SelectedIndex;
            if(selected<0 || selected>=imports.Count){PreparationStatus.Text="Import a matching game first, then refresh imports.";return;}
            StopWifi();
            string source=imports[selected];busy=true;cancellation=new CancellationTokenSource();CancelButton.Visibility=Visibility.Visible;
            PrepareButton.IsEnabled=false;PlayButton.IsEnabled=false;Progress.IsIndeterminate=true;
            string outcome;
            try
            {
                var profile=await Task.Run(()=>GameVersionSupport.Inspect(Path.Combine(source,"Spider-Man.exe")));
                if(!await ConfirmExperimentalVersion(profile)){outcome="Preparation cancelled; original files preserved.";return;}
                DiagnosticLog.Line("prepare start import="+source);
                PreparationStatus.Text="Verifying game archives…";
                var token=cancellation.Token;
                await Task.Run(()=>GamePreparation.Prepare(source,text=>
                {
                    DiagnosticLog.Line("prepare "+text);
                    GameStorage.WriteText(Path.Combine(ApplicationData.Current.LocalFolder.Path,"preparation-status.txt"),DateTime.UtcNow.ToString("o")+"\n"+text);
                    var ignored=Dispatcher.RunAsync(CoreDispatcherPriority.Low,()=>{if(busy){PreparationStatus.Text=text;Status.Text=text;}});
                },token));
                outcome="Game files prepared and verified. Press Play to check game-app setup.";
                DiagnosticLog.Line("prepare ok");
            }
            catch(OperationCanceledException){outcome="Preparation cancelled. Select the same import to resume; its archives are preserved.";}
            catch(Exception ex)
            {
                DiagnosticLog.Error("prepare",ex);
                outcome="Preparation stopped: "+ex.Message+" — Settings → Copy debug log";
            }
            finally{cancellation.Dispose();cancellation=null;busy=false;Progress.IsIndeterminate=false;CancelButton.Visibility=Visibility.Collapsed;}
            RefreshPreparation();PreparationStatus.Text=outcome;Status.Text=outcome;
            try{GameStorage.WriteText(Path.Combine(ApplicationData.Current.LocalFolder.Path,"preparation-status.txt"),DateTime.UtcNow.ToString("o")+"\n"+outcome);}catch{}
        }
        async void Play_Click(object sender,RoutedEventArgs e){await PlayGame();}
        async Task RepairRuntime(bool playAfter=false)
        {
            if(busy || (receiver!=null && receiver.Uploading))return;
            busy=true;StopWifi();PlayButton.IsEnabled=false;PrepareButton.IsEnabled=false;
            Progress.IsIndeterminate=true;
            string outcome;
            try
            {
                PreparationStatus.Text=Status.Text="Checking the installed game app and startup settings…";
                await RuntimeInstallation.Repair(playAfter);
                outcome=CombinedRuntime.Enabled?(playAfter?"Game launch requested.":"Included runtime verified; missing startup settings restored."):(playAfter?"Repairing the game app. It will launch automatically when startup settings are ready.":"Restoring missing startup settings...");
                if(!CombinedRuntime.Enabled)repairTimer.Start();
            }
            catch(Exception ex){DiagnosticLog.Error("repair",ex);outcome="Game app installation stopped (0x"+ex.HResult.ToString("X8")+"): "+ex.Message;}
            finally{busy=false;Progress.IsIndeterminate=false;}
            File.WriteAllText(Path.Combine(ApplicationData.Current.LocalFolder.Path,"runtime-repair-result.txt"),DateTime.UtcNow.ToString("o")+"\n"+outcome);
            RefreshPreparation();Status.Text=outcome;ShowRuntimeSetupResult();
        }
        async Task PlayGame(bool allowRepair=true)
        {
            if(busy || (receiver!=null && receiver.Uploading))return;
            bool repair=false;
            busy=true;PlayButton.IsEnabled=false;PrepareButton.IsEnabled=false;
            try
            {
                StopWifi();
                if(!await ConfirmExperimentalVersion(GamePreparation.Profile)){PreparationStatus.Text="Launch cancelled; game files preserved.";return;}
                if(!GamePreparation.Ready())throw new IOException("Import and prepare your game files first. Repair can restore the game app, but cannot replace missing game content.");
                cancellation=new CancellationTokenSource();CancelButton.Visibility=Visibility.Visible;Progress.IsIndeterminate=true;
                PreparationStatus.Text=Status.Text="Checking shader preparation files...";
                await Task.Run(()=>GamePreparation.EnsurePipelines(text=>
                {
                    var ignored=Dispatcher.RunAsync(CoreDispatcherPriority.Low,()=>{if(busy){PreparationStatus.Text=text;Status.Text=text;}});
                },cancellation.Token));
                PreparationStatus.Text="Checking the game app...";
                if(allowRepair && await RuntimeInstallation.NeedsRepair())repair=true;
                else
                {
                    bool started=await GamePreparation.Play();
                    if(!started && allowRepair)repair=true;
                    else PreparationStatus.Text=started?"Game launch requested.":"Setup completed, but Xbox could not open the game. Retry Play or check Dev Home. Your game files are preserved.";
                }
                if(repair)PreparationStatus.Text="Game app missing or incomplete. Starting automatic repair...";
                GameStorage.WriteText(Path.Combine(ApplicationData.Current.LocalFolder.Path,"launch-status.txt"),DateTime.UtcNow.ToString("o")+"\n"+PreparationStatus.Text);
            }
            catch(Exception ex){DiagnosticLog.Error("play",ex);PreparationStatus.Text="Launch: "+ex.Message+" — Settings → Copy debug log";}
            finally
            {
                if(cancellation!=null){cancellation.Dispose();cancellation=null;}
                CancelButton.Visibility=Visibility.Collapsed;Progress.IsIndeterminate=false;
                busy=false;
                string outcome=PreparationStatus.Text;RefreshPreparation();Status.Text=PreparationStatus.Text=outcome;
            }
            if(repair)await RepairRuntime(true);
        }
        async Task<List<ExpectedFile>> LoadExpected()
        {
            var manifest = await StorageFile.GetFileFromApplicationUriAsync(new Uri("ms-appx:///SteamFiles.json"));
            var rows = JsonArray.Parse(await FileIO.ReadTextAsync(manifest));
            var expected = new List<ExpectedFile>();
            foreach (var value in rows)
            {
                var obj = value.GetObject();
                expected.Add(new ExpectedFile { Path = obj.GetNamedString("path"), Size = (long)obj.GetNamedNumber("size"), Hash = obj.GetNamedString("sha256") });
            }
            return expected;
        }
        async void Steam_Click(object sender, RoutedEventArgs e){await DownloadFromSteam();}
        async void SteamPassword_Click(object sender,RoutedEventArgs e){await DownloadFromSteam(true);}
        async Task DownloadFromSteam(bool usePassword=false,LanguagePack languageOnly=null)
        {
            if (busy || (receiver != null && receiver.Uploading)) return;
            StopWifi();
            busy = true;
            downloadActive=true;CancelButton.Visibility=Visibility.Visible;
            int generation=++downloadGeneration;
            lastProgressSequence=0;
            TransferSize.Text=TransferStats.Text=TransferPercent.Text="";
            Progress.Value=0;Progress.IsIndeterminate=true;
            cancellation = new CancellationTokenSource();
            SteamButton.IsEnabled = SteamPasswordButton.IsEnabled = false;
            SteamLanguageList.IsEnabled=false;DownloadLanguageButton.IsEnabled=false;
            PlayButton.IsEnabled=PrepareButton.IsEnabled=false;
            int workers=SelectedWorkers();
            string outcome="Steam files downloaded and verified. Choose Prepare game to enable Play.";
            string local = null;
            SteamLoginCredentials login=null;
            try
            {
                local = GameStorage.OpenRoot();
                var selected=languageOnly??SelectedSteamLanguage();
                var catalogue=GamePreparation.LoadLanguagePacks();
                var expected = languageOnly!=null?new List<ExpectedFile>{selected.File}:SteamLanguages.SelectFiles(await LoadExpected(),catalogue,selected.Language);
                if(languageOnly!=null&&!GamePreparation.Ready())throw new IOException("Prepare the game before adding a voice pack.");
                var free = GameStorage.FreeBytes(local);
                var token = cancellation.Token;
                if(usePassword)login=await AskSteamCredentials(token);
                await Task.Run(() => SteamDownload.RunAsync(local, expected, free,
                    text=>SteamStatus(text,generation), ShowSteamQr, token,
                    snapshot=>SteamProgress(snapshot,generation),workers,login,usePassword?new SteamGuardUi(this):null,selected.Language,languageOnly!=null));
                if(languageOnly!=null) {
                    await Task.Run(()=>{
                        var folder=Path.Combine(local,"SteamLanguages",selected.Language);
                        SteamLanguages.InstallVoice(folder,LanguageGameRoot,selected,token);
                        GameLanguageSettings.Apply(LanguageGameRoot,selected.Language,LanguageCatalogue());
                    });
                    outcome=selected.Label+" voice pack installed and selected. Restart the game to apply.";
                } else outcome=selected.Label+" audio and game files verified. Choose Prepare game to enable Play.";
                Progress.Value=100;TransferPercent.Text="100%";
            }
            catch (OperationCanceledException) { outcome="Download stopped. Sign in again to verify and resume partial files."; }
            catch (Exception ex) { outcome="Steam download stopped: " + ex.Message; }
            finally
            {
                login?.Dispose();
                downloadActive=false;++downloadGeneration;CancelButton.Visibility=Visibility.Collapsed;
                ShowSteamQr(null);
                cancellation.Dispose(); cancellation = null;
                busy = false; SteamButton.IsEnabled = SteamPasswordButton.IsEnabled = true;
                SteamLanguageList.IsEnabled=true;RefreshGameLanguages();
                Progress.IsIndeterminate=false;TransferStats.Text="";
                RefreshPreparation();RefreshStorage();
                SteamInfo.Text=Status.Text=outcome;
            }
        }
        void SteamStatus(string text,int generation)
        {
            var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
            {if(downloadActive && generation==downloadGeneration){SteamInfo.Text=text;Status.Text=text;}});
        }
        void SteamProgress(DownloadProgressSnapshot snapshot,int generation)
        {
            var ignored=Dispatcher.RunAsync(CoreDispatcherPriority.Low,()=>
            {
                if(!downloadActive || generation!=downloadGeneration || snapshot.Sequence<=lastProgressSequence)return;
                lastProgressSequence=snapshot.Sequence;
                bool downloading=snapshot.Stage=="Downloading";
                Progress.IsIndeterminate=!downloading;
                Progress.Value=snapshot.TotalBytes==0?0:100.0*snapshot.CompletedBytes/snapshot.TotalBytes;
                TransferSize.Text=snapshot.TotalBytes==0?"":(snapshot.CompletedBytes/1073741824.0).ToString("F2")+" / "+(snapshot.TotalBytes/1073741824.0).ToString("F2")+" GiB";
                TransferPercent.Text=snapshot.TotalBytes==0?"":Progress.Value.ToString("F1")+"%";
                string eta=double.IsNaN(snapshot.SecondsRemaining)?"Estimating time…":snapshot.SecondsRemaining<60?"Less than a minute left":"About "+Math.Ceiling(snapshot.SecondsRemaining/60).ToString("F0")+" min left";
                TransferStats.Text=downloading?(snapshot.NetworkBytesPerSecond/1000000.0).ToString("F1")+" MB/s  ·  "+eta:snapshot.Stage;
                if(downloading)Status.Text="Downloading game";
            });
        }
        void ShowSteamQr(string url)
        {
            int generation = Interlocked.Increment(ref qrGeneration);
            var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                if (generation != qrGeneration) return;
                if (url == null) { SteamQr.Source = null; SteamQr.Visibility = Visibility.Collapsed; SteamQrState("hidden"); return; }
                try
                {
                    using (var generator = new QRCodeGenerator())
                    using (var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M))
                    using (var qr = new PngByteQRCode(data))
                    using (var input = new MemoryStream(qr.GetGraphic(6)))
                    {
                        var bitmap = new BitmapImage();
                        await bitmap.SetSourceAsync(input.AsRandomAccessStream());
                        if (generation != qrGeneration) return;
                        SteamQr.Source = bitmap; SteamQr.Visibility = Visibility.Visible;
                        SteamQrState("visible");
                        SteamInfo.Text = "Scan with Steam's mobile app and approve Spider-Man Installer on this device.";
                    }
                }
                catch { SteamQrState("failed");SteamInfo.Text = "Unable to show Steam's sign-in code. Cancel and try again."; cancellation?.Cancel(); }
            });
        }
        void SteamQrState(string state)
        {
            try{File.WriteAllText(Path.Combine(ApplicationData.Current.LocalFolder.Path,"steam-qr-state.txt"),state+" "+DateTime.UtcNow.ToString("o"));}catch{}
        }
        async void Scan_Click(object sender, RoutedEventArgs e)
        {
            if (busy) return;
            try
            {
                busy = true;
                Status.Text = "Looking for USB storage…";
                sources.Clear(); ArchiveList.Items.Clear();
                var drives = await KnownFolders.RemovableDevices.GetFoldersAsync();
                foreach (var drive in drives)
                {
                    await AddFolderCandidate(drive, drive.Name);
                    await AddDefaultDump(drive);
                    foreach (var folder in await drive.GetFoldersAsync())
                        await AddFolderCandidate(folder, drive.Name + " / " + folder.Name);
                    foreach (var file in await drive.GetFilesAsync())
                        if (file.FileType.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                        { sources.Add(file); ArchiveList.Items.Add("ZIP: " + drive.Name + " / " + file.Name); }
                }
                await AddPathCandidate(GameStorage.ExternalDefault, @"E:\dumps\SpiderMan");
                ArchiveList.SelectedIndex = sources.Count == 0 ? -1 : 0;
                Status.Text = sources.Count == 0 ? "No game folder or ZIP found. Put the Steam game folder at E:\\dumps\\SpiderMan (or any USB folder with asset_archive and Spider-Man.exe), then scan again." : sources.Count + " source(s) found. Select a folder, then Use selected folder to play from the SSD, or Copy to game storage.";
            }
            catch (Exception ex) { DiagnosticLog.Error("usb-scan", ex); Status.Text = "USB scan: " + ex.Message; }
            finally { busy = false; }
        }
        async Task AddFolderCandidate(StorageFolder candidate, string label)
        {
            try
            {
                await candidate.GetFolderAsync("asset_archive");
                if (sources.OfType<StorageFolder>().Any(f => !String.IsNullOrEmpty(f.Path) && f.Path.Equals(candidate.Path, StringComparison.OrdinalIgnoreCase))) return;
                sources.Add(candidate); ArchiveList.Items.Add("Folder: " + label);
            }
            catch (FileNotFoundException) { }
            catch (UnauthorizedAccessException) { }
        }
        async Task AddDefaultDump(StorageFolder drive)
        {
            try
            {
                var dumps = await drive.GetFolderAsync("dumps");
                await AddFolderCandidate(dumps, drive.Name + " / dumps");
                await AddFolderCandidate(await dumps.GetFolderAsync("SpiderMan"), drive.Name + " / dumps / SpiderMan");
            }
            catch (FileNotFoundException) { }
            catch (UnauthorizedAccessException) { }
        }
        async Task AddPathCandidate(string path, string label)
        {
            try
            {
                if (!GameStorage.LooksLikeGame(path)) return;
                await AddFolderCandidate(await StorageFolder.GetFolderFromPathAsync(path), label);
            }
            catch (Exception) { }
        }
        async void Import_Click(object sender, RoutedEventArgs e)
        {
            if (busy) return;
            int index = ArchiveList.SelectedIndex;
            if (index < 0 || index >= sources.Count) { Status.Text = "Find and select a game folder or ZIP first."; return; }
            var folder = sources[index] as StorageFolder;
            if (folder != null) await ImportFolder(folder);
            else await ImportFile((StorageFile)sources[index]);
        }
        async void UseFolder_Click(object sender, RoutedEventArgs e)
        {
            if (busy) return;
            int index = ArchiveList.SelectedIndex;
            if (index < 0 || index >= sources.Count) { Status.Text = "Find and select a game folder first."; return; }
            var folder = sources[index] as StorageFolder;
            if (folder == null) { Status.Text = "Use selected folder works on a game folder, not a ZIP. Choose Copy to game storage for ZIPs."; return; }
            await UseFolder(folder);
        }
        async Task UseFolder(StorageFolder folder)
        {
            if (receiver != null && receiver.Uploading) { Status.Text = "Wait for the Wi-Fi upload to finish first."; return; }
            if (String.IsNullOrEmpty(folder.Path)) { Status.Text = "Xbox did not expose this folder's path. Copy the game to E:\\dumps\\SpiderMan on a PC."; return; }
            StopWifi();
            busy = true; reportActive = true; lastUpdate = DateTime.MinValue;
            cancellation = new CancellationTokenSource();
            try
            {
                GameStorage.ApplyPickedFolder(folder);
                GameStorage.RegisterImport(folder.Path);
                BeginImport(); Progress.Value = 0; Status.Text = "Verifying the game folder in place…";
                var expected = await LoadExpected();
                var source = new UsbFolderSource(folder);
                var token = cancellation.Token;
                await Task.Run(() => FolderImportEngine.VerifyInPlaceAsync(source, folder.Path, expected, Report, token, GamePreparation.LoadLanguagePacks()));
                CompleteImport();
                Status.Text = "Game folder verified on " + GameStorage.Describe(folder.Path) + ".\n" + folder.Path + "\nFiles were not copied. Choose Prepare game to enable Play.";
                TransferStats.Text = "Verified in place — choose Prepare game.";
            }
            catch (OperationCanceledException) { reportActive = false; Status.Text = "Verification cancelled. The SSD folder was not changed."; }
            catch (Exception ex) { DiagnosticLog.Error("use-folder", ex); reportActive = false; Status.Text = "Folder verification stopped: " + ex.Message + "\nYour USB files were not changed."; }
            finally { reportActive = false; cancellation.Dispose(); cancellation = null; busy = false; CancelButton.Visibility = Visibility.Collapsed; RefreshPreparation(); RefreshStorage(); }
        }
        async void Incoming_Click(object sender, RoutedEventArgs e)
        {
            if (busy || (receiver != null && receiver.Uploading)) return;
            try { await ImportFile(null, Path.Combine(GameStorage.OpenRoot(), "received-game.zip")); }
            catch (Exception ex) { Status.Text = "No complete upload: " + ex.Message; }
        }
        async Task ImportFile(StorageFile file, string receivedPath = null)
        {
            if (receiver != null && receiver.Uploading) { Status.Text = "Wait for the Wi-Fi upload to finish first."; return; }
            StopWifi();
            busy = true; reportActive = true; lastUpdate = DateTime.MinValue; cancellation = new CancellationTokenSource();
            string local = null;
            string destination = null;
            try
            {
                BeginImport(); Status.Text = "Checking ZIP, Steam version and voice packs…"; Progress.Value = 0;
                local = GameStorage.OpenRoot();
                var expected = await LoadExpected();
                long free = GameStorage.FreeBytes(local);
                destination = Path.Combine(local, "SteamImport-" + Guid.NewGuid().ToString("N"));
                var token = cancellation.Token;
                using (var input = file != null ? await file.OpenStreamForReadAsync() : GameStorage.OpenRead(receivedPath))
                    await Task.Run(() => ImportEngine.Import(input, destination, expected, free, Report, token, GamePreparation.LoadLanguagePacks()));
                if (receivedPath != null && receivedPath.Equals(Path.Combine(local, "received-game.zip"), StringComparison.OrdinalIgnoreCase))
                    GameStorage.DeleteFile(receivedPath);
                CompleteImport(); Status.Text = "Steam files copied and verified.\n" + destination + "\nChoose Prepare game below to enable Play.";
                Progress.Value = 100;
            }
            catch (OperationCanceledException) { Status.Text = "Import cancelled. Incomplete copy retained in the import destination."; }
            catch (Exception ex) { Status.Text = "Import stopped: " + ex.Message + "\nYour SSD and existing game were not changed."; }
            finally { reportActive = false; cancellation.Dispose(); cancellation = null; busy = false; CancelButton.Visibility=Visibility.Collapsed; RefreshPreparation();RefreshStorage(); }
        }
        async Task ImportFolder(StorageFolder folder)
        {
            if (receiver != null && receiver.Uploading) { Status.Text = "Wait for the Wi-Fi upload to finish first."; return; }
            StopWifi();
            busy = true; reportActive = true; lastUpdate = DateTime.MinValue;
            cancellation = new CancellationTokenSource();
            string local = null;
            string destination = null;
            try
            {
                BeginImport(); Progress.Value = 0; Status.Text = "Checking game folder, version and voice packs…";
                local = GameStorage.OpenRoot();
                var expected = await LoadExpected();
                long free = GameStorage.FreeBytes(local);
                destination = Path.Combine(local, "SteamImport-" + Guid.NewGuid().ToString("N"));
                var source = new UsbFolderSource(folder);
                var token = cancellation.Token;
                await Task.Run(() => FolderImportEngine.ImportAsync(source, destination, expected, free, Report, token, GamePreparation.LoadLanguagePacks()));
                CompleteImport();
                Status.Text = "Game folder copied and verified.\n" + destination + "\nChoose Prepare game below to enable Play.";
            }
            catch (OperationCanceledException) { reportActive = false; Status.Text = "Import cancelled. Incomplete copy retained in the import destination; no success receipt was written."; }
            catch (Exception ex) { reportActive = false; Status.Text = "Folder import stopped: " + ex.Message + "\nYour USB files were not changed."; }
            finally { reportActive = false; cancellation.Dispose(); cancellation = null; busy = false; CancelButton.Visibility=Visibility.Collapsed; RefreshPreparation();RefreshStorage(); }
        }
        void BeginImport()
        {
            Interlocked.Exchange(ref importDone,0);Interlocked.Exchange(ref importTotal,0);
            TransferSize.Text="";TransferPercent.Text="";TransferStats.Text="Checking files…";
            CancelButton.Visibility=Visibility.Visible;
        }
        void CompleteImport()
        {
            reportActive=false;Progress.IsIndeterminate=false;Progress.Value=100;
            long total=Interlocked.Read(ref importTotal);
            TransferSize.Text=(total/1073741824.0).ToString("F2")+" / "+(total/1073741824.0).ToString("F2")+" GiB";
            TransferPercent.Text="100%";TransferStats.Text="Copy complete and verified — choose Prepare game.";
            CancelButton.Visibility=Visibility.Collapsed;
        }
        void Report(long done, long total, string file)
        {
            Interlocked.Exchange(ref importDone,done);Interlocked.Exchange(ref importTotal,total);
            if ((DateTime.UtcNow - lastUpdate).TotalMilliseconds < 500 && done != total) return;
            lastUpdate = DateTime.UtcNow;
            var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
            {
                if (!reportActive) return;
                Progress.IsIndeterminate=false;Progress.Value=total==0?0:100.0*done/total;
                Status.Text="Importing "+file;
                TransferSize.Text=(done/1073741824.0).ToString("F2")+" / "+(total/1073741824.0).ToString("F2")+" GiB";
                TransferPercent.Text=Progress.Value.ToString("F1")+"%";TransferStats.Text="Copying and verifying files";
            });
        }
        async void Wifi_Click(object sender, RoutedEventArgs e){await StartWifi();}
        void StopWifi()
        {
            ++wifiGeneration;receiver?.Dispose();receiver=null;
            WifiQr.Source=null;WifiQr.Visibility=WifiQrHint.Visibility=Visibility.Collapsed;
            WifiAddress.Text=WifiPairing.Text="";
            WifiPairingCard.Visibility=Visibility.Collapsed;
        }
        async Task StartWifi()
        {
            if(busy || (receiver!=null && receiver.Uploading))return;
            StopWifi();int generation=wifiGeneration;busy=true;wifiComplete=false;WifiButton.IsEnabled=false;
            try
            {
                var expected=await LoadExpected();
                var file=await StorageFile.GetFileFromApplicationUriAsync(new Uri("ms-appx:///WifiTransfer.html"));
                string page=await FileIO.ReadTextAsync(file);
                if(generation!=wifiGeneration)return;
                var candidate=new WifiReceiver(GameStorage.OpenRoot(),page,expected,GamePreparation.LoadLanguagePacks(),text=>
                {var ignored=Dispatcher.RunAsync(CoreDispatcherPriority.Normal,()=>{if(generation==wifiGeneration)WifiInfo.Text=text;});},
                (done,total,name)=>WifiProgress(done,total,name,generation),
                complete=>{var ignored=Dispatcher.RunAsync(CoreDispatcherPriority.Normal,()=>
                {
                    if(generation!=wifiGeneration)return;
                    wifiComplete=true;reportActive=false;Progress.Value=100;TransferPercent.Text="100%";
                    long bytes=Interlocked.Read(ref importTotal);
                    TransferSize.Text=(bytes/1073741824.0).ToString("F2")+" / "+(bytes/1073741824.0).ToString("F2")+" GiB";
                    TransferStats.Text=complete?"Files verified  -  choose Prepare game.":"ZIP received  -  choose Import received ZIP.";
                    Status.Text=TransferStats.Text;RefreshPreparation();RefreshStorage();
                });});
                receiver=candidate;await candidate.Start();
                if(generation!=wifiGeneration){candidate.Dispose();return;}
                BeginImport();Progress.Value=0;Progress.IsIndeterminate=false;reportActive=true;lastUpdate=DateTime.MinValue;
                WifiAddress.Text=candidate.Address??"http://<Xbox IP>:8077/";
                WifiPairing.Text="Pairing code: "+candidate.PairCode;
                WifiPairingCard.Visibility=Visibility.Visible;
                WifiInfo.Text="Open this address on your PC, enter the code, then choose your game folder.";
                Status.Text="Wi-Fi receiver ready";TransferStats.Text="Waiting for your PC";
                if(candidate.QrAddress!=null)
                {
                    using(var generator=new QRCodeGenerator())
                    using(var data=generator.CreateQrCode(candidate.QrAddress,QRCodeGenerator.ECCLevel.M))
                    using(var qr=new PngByteQRCode(data))
                    using(var input=new MemoryStream(qr.GetGraphic(6)))
                    {
                        var bitmap=new BitmapImage();await bitmap.SetSourceAsync(input.AsRandomAccessStream());
                        if(generation!=wifiGeneration)return;
                        WifiQr.Source=bitmap;WifiQr.Visibility=WifiQrHint.Visibility=Visibility.Visible;
                    }
                }
            }
            catch(Exception ex){if(generation==wifiGeneration){StopWifi();WifiInfo.Text="Wi-Fi receiver: "+ex.Message;CancelButton.Visibility=Visibility.Collapsed;}}
            finally{busy=false;WifiButton.IsEnabled=true;}
        }
        void WifiProgress(long done,long total,string name,int generation)
        {
            if(generation!=wifiGeneration)return;
            Interlocked.Exchange(ref importDone,done);Interlocked.Exchange(ref importTotal,total);
            if(done<total)wifiComplete=false;
            // Throttle before dispatch so network writes cannot flood the UI queue.
            if((DateTime.UtcNow-lastUpdate).TotalMilliseconds<500 && done!=total)return;
            lastUpdate=DateTime.UtcNow;
            var ignored=Dispatcher.RunAsync(CoreDispatcherPriority.Low,()=>
            {
                if(generation!=wifiGeneration || wifiComplete)return;
                Progress.IsIndeterminate=false;Progress.Value=total==0?0:100.0*done/total;
                TransferSize.Text=(done/1073741824.0).ToString("F2")+" / "+(total/1073741824.0).ToString("F2")+" GiB";
                TransferPercent.Text=Progress.Value.ToString("F1")+"%";
                Status.Text=name;TransferStats.Text="Receiving and verifying game files";
            });
        }
        void Cancel_Click(object sender, RoutedEventArgs e)
        {
            cancellation?.Cancel(); StopWifi();
            CancelButton.Visibility=Visibility.Collapsed;
            WifiInfo.Text = "Wi-Fi receiver stopped.";
        }
        async void CopyDebugLog_Click(object sender, RoutedEventArgs e)
        {
            string text = DiagnosticLog.ReadAll();
            try
            {
                var data = new DataPackage();
                data.SetText(text);
                Clipboard.SetContent(data);
                DebugLogStatus.Text = "Copied "+text.Length+" characters. Paste here in chat, or pull LocalState\\launcher-debug.log from Device Portal.";
            }
            catch (Exception ex)
            {
                DebugLogStatus.Text = "Clipboard failed: "+ex.Message+". Use Show last error or Device Portal LocalState\\launcher-debug.log";
            }
            await ShowDebugText("Debug log", text);
        }
        async void ShowLastError_Click(object sender, RoutedEventArgs e)
        {
            string text = "(no last error yet)";
            try
            {
                string path = Path.Combine(DiagnosticLog.Folder ?? "", "launcher-debug-last.txt");
                if (File.Exists(path)) text = File.ReadAllText(path);
            }
            catch (Exception ex) { text = ex.ToString(); }
            await ShowDebugText("Last error", text);
        }
        async Task ShowDebugText(string title, string text)
        {
            var box = new TextBox
            {
                Text = text ?? "",
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                MinHeight = 220
            };
            await new ContentDialog
            {
                Title = title,
                Content = new ScrollViewer { Content = box, MaxHeight = 420 },
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close,
                RequestedTheme = ElementTheme.Dark
            }.ShowAsync();
        }
    }
    static class StorageSpace
    {
        internal static async Task<long> FreeBytes(StorageFolder folder)
        {
            var properties = await folder.Properties.RetrievePropertiesAsync(new[] { "System.FreeSpace" });
            object value;
            if (!properties.TryGetValue("System.FreeSpace", out value) || value == null)
                throw new IOException("Cannot check storage space safely on this device.");
            return checked((long)Convert.ToUInt64(value));
        }
    }
}
