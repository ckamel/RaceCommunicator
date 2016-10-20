using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Media.Devices;
using Windows.Storage;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

// The Blank Page item template is documented at http://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409

namespace RaceCommunicator
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainPage : Page
    {

        private DispatcherTimer refreshTimer;
        private bool isMessageListInitialized = false;

        public MainPage()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            AudioEngine.Instance.RecordingEnabledChanged += OnRecordingEnabledChanged;
            AudioEngine.Instance.InputDevicesEnumerated += OnInputDevicesAvailable;
            AudioEngine.Instance.OutputDevicesEnumerated += OnOutputDevicesAvailable;
            this.startButton.IsEnabled = AudioEngine.Instance.CanStartRecording();

            AudioEngine.Instance.Initialize();

            SetupUIRefreshTimer();
            sliderVolumeThreshold.Value = AudioEngine.Instance.RecordingThreshold * 100;
            sliderMillisecondsBeforeRecording.Value = (int)AudioEngine.Instance.MillisecondsBeforeRecordingStart;
            sliderMillisecondAfterRecording.Value = (int)AudioEngine.Instance.MillisecondsBeforeRecordingStop;
        }

        private async void InitializeMessagesList()
        {
            Regex fileNameFormat = new Regex(@"^(\d{4})(\d{2})(\d{2})_(\d{2})_(\d{2})_(\d{2})");
            StorageFolder folderToEnumerate = AudioEngine.Instance.SaveFolder;
            foreach (var file in await folderToEnumerate.GetFilesAsync())
            {
                var match = fileNameFormat.Match(file.Name);
                if (match.Groups.Count == 7)
                {
                    string dateString = match.Groups[0].Value;
                    DateTime messageTime = DateTime.ParseExact(dateString, "yyyyMMdd_HH_mm_ss", CultureInfo.InvariantCulture);
                    messagesList.Items.Add(messageTime);
                }
            }
            isMessageListInitialized = true;
        }

        private void SetupUIRefreshTimer()
        {
            refreshTimer = new DispatcherTimer();
            refreshTimer.Tick += RefreshUI;
            refreshTimer.Interval = TimeSpan.FromMilliseconds(50);
            refreshTimer.Start();
        }

        private void RefreshUI(object sender, object e)
        {
            decibelTextbox.Text = (AudioEngine.Instance.LastObservedDecibelValue * 100).ToString();

            string buttonTextPrefix, buttonTextSuffix = string.Empty;
            if (AudioEngine.Instance.IsMonitoring)
            {
                buttonTextPrefix = "Stop";
            }
            else
            {
                buttonTextPrefix = "Start";
            }

            if (AudioEngine.Instance.IsRecording)
            {
                buttonTextSuffix = "Recording...";
            }
            else if (AudioEngine.Instance.IsMonitoring)
            {
                buttonTextSuffix = "Monitoring...";
            }

            startButton.Content = $"{buttonTextPrefix} - {buttonTextSuffix}";

            if (AudioEngine.Instance.IsInitialized && !isMessageListInitialized)
            {
                InitializeMessagesList();
            }
        }

        private async void OnInputDevicesAvailable(object sender, EventArgs e)
        {
            await Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(CoreDispatcherPriority.Normal,
            () =>
            {
                recordingDeviceComboBox.Items.Clear();
                recordingDeviceComboBox.Items.Add(AudioDeviceWrapper.NullDevice());

                foreach (var device in AudioEngine.Instance.InputDevices)
                {
                    AudioDeviceWrapper wrapper = new AudioDeviceWrapper(device);
                    recordingDeviceComboBox.Items.Add(wrapper);
                    if (recordingDeviceComboBox.SelectedItem == null)
                    {
                        recordingDeviceComboBox.SelectedItem = wrapper;
                    }
                }
            });
        }

        private async void OnOutputDevicesAvailable(object sender, EventArgs e)
        {
            await Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(CoreDispatcherPriority.Normal,
            () =>
            {
                outputDeviceComboBox.Items.Clear();
                foreach (var device in AudioEngine.Instance.OutputDevices)
                {
                    AudioDeviceWrapper wrapper = new AudioDeviceWrapper(device);
                    outputDeviceComboBox.Items.Add(wrapper);
                }

                if (AudioEngine.Instance.SelectedOutputDevice != null)
                {
                    outputDeviceComboBox.SelectedItem = outputDeviceComboBox.Items.Cast<AudioDeviceWrapper>()
                        .Where(d => d.WindowsDeviceInformation == AudioEngine.Instance.SelectedOutputDevice);
                }

                if (outputDeviceComboBox.SelectedItem == null && outputDeviceComboBox.Items.Count > 0)
                {
                    outputDeviceComboBox.SelectedIndex = 0;
                }
            });
            
        }

        private void recordingDeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ComboBox inputSelection = sender as ComboBox;
            AudioEngine.Instance.SelectInputDevice(((AudioDeviceWrapper)inputSelection.SelectedItem).WindowsDeviceInformation);
        }

        private void OnRecordingEnabledChanged(object sender, bool isRecordingEnabled)
        {
            this.startButton.IsEnabled = isRecordingEnabled;
        }

        private void outputDeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ComboBox inputSelection = sender as ComboBox;
            AudioEngine.Instance.SelectOutputDevice(((AudioDeviceWrapper)inputSelection.SelectedItem).WindowsDeviceInformation);
        }

        private void startButton_Click(object sender, RoutedEventArgs e)
        {
            Button eventSender = sender as Button;
            if (AudioEngine.Instance.IsMonitoring)
            {
                AudioEngine.Instance.StopMonitoring();
            }
            else
            {
                AudioEngine.Instance.StartMonitoring();
            }
        }

        private void slider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            Slider eventSender = sender as Slider;
            if (eventSender == sliderVolumeThreshold)
            {
                AudioEngine.Instance.RecordingThreshold = e.NewValue / 100.0;
            }
            else if (eventSender == sliderMillisecondAfterRecording)
            {
                AudioEngine.Instance.MillisecondsBeforeRecordingStop = (int)e.NewValue;
            }
            else if (eventSender == sliderMillisecondsBeforeRecording)
            {
                AudioEngine.Instance.MillisecondsBeforeRecordingStart = (int)e.NewValue;
            }
        }

        private void messagesList_Tapped(object sender, TappedRoutedEventArgs e)
        {
            //TODO find the file and play it.
        }
    }
}
