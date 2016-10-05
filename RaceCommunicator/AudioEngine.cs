using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Capture;
using Windows.Media.Devices;
using Windows.Media.MediaProperties;
using Windows.Media.Render;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace RaceCommunicator
{
    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    unsafe interface IMemoryBufferByteAccess
    {
        void GetBuffer(out byte* buffer, out uint capacity);
    }

    public sealed class AudioEngine
    {
        public static readonly AudioEngine Instance = new AudioEngine();

        private List<DeviceInformation> inputDevices = new List<DeviceInformation>();
        private List<DeviceInformation> outputDevices = new List<DeviceInformation>();

        public IReadOnlyList<DeviceInformation> InputDevices
        {
            get { return this.inputDevices; }
        }

        public IReadOnlyList<DeviceInformation> OutputDevices
        {
            get { return this.outputDevices; }
        }

        private AudioGraph currentGraph;

        public DeviceInformation SelectedInputDevice
        {
            get;
            private set;
        }
        public DeviceInformation SelectedOutputDevice
        {
            get;
            private set;
        }
        private event EventHandler<bool> recordingEnabledStatusChanged;
        private event EventHandler inputDevicesEnumerated;
        private event EventHandler outputDevicesEnumerated;

        private AudioDeviceInputNode deviceInputNode;
        private AudioFileOutputNode fileOutputNode;
        private AudioDeviceOutputNode deviceOutputNode;
        private AudioFrameOutputNode monitoringNode;

        private StorageFolder fileSaveFolder;
        private bool isRecording = false;
        private int numSamplesUnderThreshold = 0;
        private int numSamplesOverThreshold = 0;
        private int numSamplesToWaitBeforeRecordingStop = 100;
        private int numSamplesToWaitBeforeRecordingStart = 1;

        public double RecordingThreshold
        {
            get; set;
        }

        public double LastObservedDecibelValue
        {
            get;
            private set;
        }

        public async Task Initialize()
        {
            LastObservedDecibelValue = 0;
            RecordingThreshold = 0.1;
            
            await InitStorage().ConfigureAwait(false);

            await EnumerateInputDevices().ConfigureAwait(false);
            await EnumerateOutputDevices().ConfigureAwait(false);

            await CreateAudioGraph().ConfigureAwait(continueOnCapturedContext: false);            
            //StartMonitoring();
        }

        public async Task EnumerateInputDevices(bool forceEnumeration = false)
        {
            DeviceInformationCollection enumeratedDevices;
            if (inputDevices == null || !inputDevices.Any() || forceEnumeration)
            {
                string captureSelector = MediaDevice.GetAudioCaptureSelector();
                enumeratedDevices = await DeviceInformation.FindAllAsync(captureSelector);
                inputDevices.Clear();
                foreach (var device in enumeratedDevices)
                {
                    inputDevices.Add(device);
                }
                OnInputDevicesEnumerated(EventArgs.Empty);
            }
        }

        public async Task EnumerateOutputDevices(bool forceEnumeration = false)
        {
            DeviceInformationCollection enumeratedDevices;
            if (outputDevices == null || !outputDevices.Any() || forceEnumeration)
            {
                string renderSelector = MediaDevice.GetAudioRenderSelector();
                enumeratedDevices = await DeviceInformation.FindAllAsync(renderSelector);
                outputDevices.Clear();
                foreach (var device in enumeratedDevices)
                {
                    outputDevices.Add(device);
                }
            }
            OnOutputDevicesEnumerated(EventArgs.Empty);
        }

        public void SelectInputDevice(DeviceInformation selectedDevice)
        {
            if (SelectedInputDevice == selectedDevice)
            {
                return;
            }

            bool canStartRecordingBefore = CanStartRecording();
            SelectedInputDevice = selectedDevice;
            bool canStartRecordingAfter = CanStartRecording();
            if (canStartRecordingAfter != canStartRecordingBefore)
            {
                OnRecordingEnabledChanged(canStartRecordingAfter);
            }
        }

        public void SelectOutputDevice(DeviceInformation selectedDevice)
        {
            if (SelectedOutputDevice == selectedDevice)
            {
                return;
            }

            bool canStartRecordingBefore = CanStartRecording();
            SelectedOutputDevice = selectedDevice;
            bool canStartRecordingAfter = CanStartRecording();
            if (canStartRecordingAfter != canStartRecordingBefore)
            {
                OnRecordingEnabledChanged(canStartRecordingAfter);
            }
        }

        public bool CanStartRecording()
        {
            return SelectedInputDevice != null && SelectedOutputDevice != null;
        }

        public void StartMonitoring()
        {
            currentGraph.Start();
        }

        private void StartRecording()
        {
            isRecording = true;
            fileOutputNode.Start();
        }

        private void StopRecording()
        {
            isRecording = false;
            fileOutputNode.Stop();
        }

        public event EventHandler<bool> RecordingEnabledChanged
        {
            add
            {
                if (this.recordingEnabledStatusChanged == null ||
                    !this.recordingEnabledStatusChanged.GetInvocationList().Contains(value))
                {
                    this.recordingEnabledStatusChanged += value;
                }
            }
            remove
            {
                this.recordingEnabledStatusChanged -= value;
            }
        }

        public event EventHandler InputDevicesEnumerated
        {
            add
            {
                if (this.inputDevicesEnumerated == null ||
                    !this.inputDevicesEnumerated.GetInvocationList().Contains(value))
                {
                    this.inputDevicesEnumerated += value;
                }
            }
            remove
            {
                this.inputDevicesEnumerated -= value;
            }
        }

        public event EventHandler OutputDevicesEnumerated
        {
            add
            {
                if (this.outputDevicesEnumerated == null ||
                    !this.outputDevicesEnumerated.GetInvocationList().Contains(value))
                {
                    this.outputDevicesEnumerated += value;
                }
            }
            remove
            {
                this.outputDevicesEnumerated -= value;
            }
        }
        
        private async Task<bool> CreateAudioGraph()
        {

            DisposeCurrentGraph();
            AudioGraphSettings settings = new AudioGraphSettings(AudioRenderCategory.Speech);
            settings.DesiredSamplesPerQuantum = 100;
            settings.PrimaryRenderDevice = SelectedOutputDevice;

            CreateAudioGraphResult result = await AudioGraph.CreateAsync(settings);

            if (result.Status != AudioGraphCreationStatus.Success)
            {
                // Cannot create graph
                //rootPage.NotifyUser(String.Format("AudioGraph Creation Error because {0}", result.Status.ToString()), NotifyType.ErrorMessage);
                return false;
            }

            currentGraph = result.Graph;

            if (!await CreateInputNode())
            {
                return false;
            }

            if (!await CreateFileSaveNode())
            {
                return false;
            }

            CreateMonitoringNode();

            // Create a device output node
            if (! await CreateOutputNode())
            {
                return false;
            }

            fileOutputNode.Stop();
            deviceInputNode.AddOutgoingConnection(monitoringNode);
            deviceInputNode.AddOutgoingConnection(deviceOutputNode);
            deviceInputNode.AddOutgoingConnection(fileOutputNode);

            return true;            
            //rootPage.NotifyUser("Graph successfully created!", NotifyType.StatusMessage);
        }

        private void CreateMonitoringNode()
        {
            monitoringNode = currentGraph.CreateFrameOutputNode();
            currentGraph.QuantumProcessed += AudioGraph_QuantumProcessed;

        }

        private void AudioGraph_QuantumProcessed(AudioGraph sender, object args)
        {
            AudioFrame frame = monitoringNode.GetFrame();
            ProcessAudioFrame(frame);

            if (LastObservedDecibelValue > RecordingThreshold && !isRecording)
            {
                numSamplesOverThreshold++;
                if (numSamplesOverThreshold > numSamplesToWaitBeforeRecordingStart)
                {
                    StartRecording();
                    numSamplesUnderThreshold = 0;
                }
            }
            else if (LastObservedDecibelValue < RecordingThreshold && isRecording)
            {
                numSamplesUnderThreshold++;                

                if (numSamplesUnderThreshold > numSamplesToWaitBeforeRecordingStop)
                {
                    StopRecording();
                    numSamplesOverThreshold = 0;
                }
            }
        }

        unsafe private void ProcessAudioFrame(AudioFrame frame)
        {
            using (AudioBuffer buffer = frame.LockBuffer(AudioBufferAccessMode.Write))
            using (IMemoryBufferReference reference = buffer.CreateReference())
            {
                byte* dataInBytes;
                uint capacityInBytes;
                float* dataInFloat;

                // Get the buffer from the AudioFrame
                ((IMemoryBufferByteAccess)reference).GetBuffer(out dataInBytes, out capacityInBytes);
                if (capacityInBytes == 0)
                {
                    LastObservedDecibelValue = 0;
                    return;
                }

                dataInFloat = (float*)dataInBytes;
                uint numFloats = capacityInBytes / sizeof(float);

                double sum = 0;
                for (uint i = 0; i < numFloats; ++i)
                {
                    double sample = Math.Abs(dataInFloat[i]);
                    sum += sample;
                }

                double rms = sum / numFloats;
                LastObservedDecibelValue = rms; //20 * Math.Log10(rms);
            }
        }

        private async Task<bool> CreateOutputNode()
        {
            CreateAudioDeviceOutputNodeResult deviceOutputNodeResult = await currentGraph.CreateDeviceOutputNodeAsync();
            if (deviceOutputNodeResult.Status != AudioDeviceNodeCreationStatus.Success)
            {
                // Cannot create device output node
                //rootPage.NotifyUser(String.Format("Audio Device Output unavailable because {0}", deviceOutputNodeResult.Status.ToString()), NotifyType.ErrorMessage);
                //outputDeviceContainer.Background = new SolidColorBrush(Colors.Red);
                return false;
            }
            deviceOutputNode = deviceOutputNodeResult.DeviceOutputNode;
            //rootPage.NotifyUser("Device Output connection successfully created", NotifyType.StatusMessage);
            //outputDeviceContainer.Background = new SolidColorBrush(Colors.Green);
            return true;
        }

        private async Task<bool> CreateInputNode()
        {
            // Create a device input node using the default audio input device
            CreateAudioDeviceInputNodeResult deviceInputNodeResult = await currentGraph.CreateDeviceInputNodeAsync(MediaCategory.Speech,
                AudioEncodingProperties.CreatePcm(44100, 1, 16), SelectedInputDevice);

            if (deviceInputNodeResult.Status != AudioDeviceNodeCreationStatus.Success)
            {
                // Cannot create device input node
                return false;
            }

            deviceInputNode = deviceInputNodeResult.DeviceInputNode;

            return true;
        }

        private async Task<bool> CreateFileSaveNode()
        {
            string fileName = DateTime.Now.ToString("yyyyMMdd_HH_mm_ss") + ".mp3";
            StorageFile file = await fileSaveFolder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
            
            MediaEncodingProfile fileProfile = MediaEncodingProfile.CreateMp3(AudioEncodingQuality.High);
            CreateAudioFileOutputNodeResult fileOutputNodeResult = await currentGraph.CreateFileOutputNodeAsync(file, fileProfile);

            if (fileOutputNodeResult.Status != AudioFileNodeCreationStatus.Success)
            {
                // FileOutputNode creation failed
                return false;
            }
            
            fileOutputNode = fileOutputNodeResult.FileOutputNode;
            return true;

        }

        private void DisposeCurrentGraph()
        {
            if(currentGraph != null)
            {
                currentGraph.Dispose();
                currentGraph = null;
            }
        }

        private void OnRecordingEnabledChanged(bool isRecordingEnabled)
        {
            if (this.recordingEnabledStatusChanged != null)
            {
                this.recordingEnabledStatusChanged(this, isRecordingEnabled);
            }
        }

        private void OnInputDevicesEnumerated(EventArgs e)
        {
            if (this.inputDevicesEnumerated != null)
            {
                this.inputDevicesEnumerated(this, e);
            }
        }

        private void OnOutputDevicesEnumerated(EventArgs e)
        {
            if (this.outputDevicesEnumerated != null)
            {
                outputDevicesEnumerated(this, e);
            }
        }

        private async Task InitStorage()
        {
            StorageFolder rootFolder = Windows.Storage.KnownFolders.MusicLibrary;
            fileSaveFolder = await rootFolder.CreateFolderAsync("RaceCommunicator", CreationCollisionOption.OpenIfExists);
        }
    }
}
