using System;
using System.Collections.Generic;
using System.IO;
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

        private AudioGraph currentRecordingGraph;
        private AudioGraph currentPlaybackGraph;

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
        private event EventHandler newRecordingSaved;

        private AudioDeviceInputNode deviceInputNode;
        private AudioFileOutputNode fileOutputNode;
        private AudioDeviceOutputNode deviceOutputNode;
        private AudioFrameOutputNode monitoringNode;
        private StorageFolder storageFolder;
        private string lastCreatedFileName;
        private DateTime lastRecordingStartTime;
        private int recordingCount = 0;

        private AudioDeviceOutputNode playbackOutputNode;
        private AudioFileInputNode playbackFileNode;
        
        private int millisecondsUnderThreshold = 0;
        private int millisecondsOverThreshold = 0;
        public int MillisecondsBeforeRecordingStop { get; set; }
        public int MillisecondsBeforeRecordingStart { get; set; }
        private volatile bool isRecording;
        public bool IsRecording
        {
            get
            {
                return isRecording;
            }
        }

        public bool IsMonitoring { get; private set; }

        public double RecordingThreshold
        {
            get; set;
        }

        public double LastObservedDecibelValue
        {
            get;
            private set;
        }

        public StorageFolder SaveFolder
        {
            get
            {
                return storageFolder;
            }
        }

        public bool IsInitialized
        {
            get; private set;
        }

        public async Task Initialize()
        {
            LastObservedDecibelValue = 0;
            RecordingThreshold = 0.02;
            MillisecondsBeforeRecordingStop = 1500;
            MillisecondsBeforeRecordingStart = 15;
            isRecording = false;
            
            await InitStorage().ConfigureAwait(false);

            await EnumerateInputDevices().ConfigureAwait(false);
            await EnumerateOutputDevices().ConfigureAwait(false);

            await CreateRecordingAudioGraph().ConfigureAwait(continueOnCapturedContext: false);
            await CreatePlaybackAudioGraph().ConfigureAwait(continueOnCapturedContext: false);
            IsInitialized = true;
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
            currentRecordingGraph.Start();
            IsMonitoring = true;
        }

        public void StopMonitoring()
        {
            currentRecordingGraph.Stop();
            IsMonitoring = false;
        }

        private void StartRecording()
        {
            if (isRecording)
            {
                return;
            }

            isRecording = true;
            millisecondsOverThreshold = 0;
            millisecondsUnderThreshold = 0;
            fileOutputNode.Start();
            lastRecordingStartTime = DateTime.Now;
        }

        private void StopRecording()
        {
            if (!isRecording)
            {
                return;
            }


            isRecording = false;
            millisecondsOverThreshold = 0;
            millisecondsUnderThreshold = 0;
            fileOutputNode.Stop();
            string recordedFileName = lastCreatedFileName;
            //Recreate the node to get a new file
            CreateAndConnectFileSaveNode(deviceInputNode);
            string oldFilePath = Path.Combine(storageFolder.Path, recordedFileName);
            string newFilePath = Path.Combine(storageFolder.Path, GetRecordingFileName());
            File.Move(oldFilePath, newFilePath);
            OnNewRecordingSaved(newFilePath);

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

        public event EventHandler NewRecordingSaved
        {
            add
            {
                if (this.newRecordingSaved == null ||
                    !this.newRecordingSaved.GetInvocationList().Contains(value))
                {
                    this.newRecordingSaved += value;
                }
            }
            remove
            {
                this.newRecordingSaved -= value;
            }
        }

        public async void Playback(DateTime recordingTime)
        {
            if (playbackFileNode != null)
            {
                playbackFileNode.Stop();
                playbackFileNode.Dispose();
                playbackFileNode = null;
            }

            string recordingFileName = GetRecordingFileName(recordingTime);
            string filePath = Path.Combine(storageFolder.Path, recordingFileName);
            StorageFile file = await StorageFile.GetFileFromPathAsync(filePath);
            
            var nodeCreationResult = await currentPlaybackGraph.CreateFileInputNodeAsync(file);
            if (nodeCreationResult.Status != AudioFileNodeCreationStatus.Success)
            {
                throw new Exception($"Failed to play back file with node creation result {nodeCreationResult.Status}");
            }
            playbackFileNode = nodeCreationResult.FileInputNode;
            playbackFileNode.AddOutgoingConnection(playbackOutputNode);            
        }

        private async Task<bool> CreateRecordingAudioGraph()
        {
            DisposeCurrentRecordingGraph();
            AudioGraphSettings settings = new AudioGraphSettings(AudioRenderCategory.Speech);
            settings.PrimaryRenderDevice = SelectedOutputDevice;

            CreateAudioGraphResult result = await AudioGraph.CreateAsync(settings);

            if (result.Status != AudioGraphCreationStatus.Success)
            {
                // Cannot create graph
                //rootPage.NotifyUser(String.Format("AudioGraph Creation Error because {0}", result.Status.ToString()), NotifyType.ErrorMessage);
                return false;
            }

            currentRecordingGraph = result.Graph;

            if (!await CreateInputNode())
            {
                return false;
            }

            if (!await CreateAndConnectFileSaveNode(deviceInputNode))
            {
                return false;
            }

            CreateMonitoringNode();

            // Create a device output node
            if (! await CreateAndConnectOutputNode(deviceInputNode))
            {
                return false;
            }

            fileOutputNode.Stop();
            deviceInputNode.AddOutgoingConnection(monitoringNode);

            return true;            
            //rootPage.NotifyUser("Graph successfully created!", NotifyType.StatusMessage);
        }

        private async Task<bool> CreatePlaybackAudioGraph()
        {
            DisposeCurrentPlaybackGraph();
            AudioGraphSettings settings = new AudioGraphSettings(AudioRenderCategory.Speech);
            settings.PrimaryRenderDevice = SelectedOutputDevice;

            CreateAudioGraphResult result = await AudioGraph.CreateAsync(settings);

            if (result.Status != AudioGraphCreationStatus.Success)
            {
                // Cannot create graph
                return false;
            }

            currentPlaybackGraph = result.Graph;

            CreateAudioDeviceOutputNodeResult deviceOutputNodeResult = await currentPlaybackGraph.CreateDeviceOutputNodeAsync();
            if (deviceOutputNodeResult.Status != AudioDeviceNodeCreationStatus.Success)
            {
                // Cannot create device output node
                return false;
            }
            playbackOutputNode = deviceOutputNodeResult.DeviceOutputNode;
            currentPlaybackGraph.Start();
            return true;
            //rootPage.NotifyUser("Graph successfully created!", NotifyType.StatusMessage);
        }

        private void CreateMonitoringNode()
        {
            monitoringNode = currentRecordingGraph.CreateFrameOutputNode();
            currentRecordingGraph.QuantumProcessed += AudioGraph_QuantumProcessed;

        }

        private void AudioGraph_QuantumProcessed(AudioGraph sender, object args)
        {
            AudioFrame frame = monitoringNode.GetFrame();
            if (frame.Duration < TimeSpan.FromMilliseconds(5))
            {
                return;
            }
            ProcessAudioFrame(frame);

            if (LastObservedDecibelValue > RecordingThreshold && !isRecording)
            {
                millisecondsOverThreshold += (int)frame.Duration.Value.TotalMilliseconds;
                if (millisecondsOverThreshold > MillisecondsBeforeRecordingStart)
                {
                    StartRecording();
                }
            }
            else if (LastObservedDecibelValue < RecordingThreshold && isRecording)
            {
                millisecondsUnderThreshold += (int)frame.Duration.Value.TotalMilliseconds;

                if (millisecondsUnderThreshold > MillisecondsBeforeRecordingStop)
                {
                    StopRecording();
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

        private async Task<bool> CreateAndConnectOutputNode(AudioDeviceInputNode inputNode)
        {
            CreateAudioDeviceOutputNodeResult deviceOutputNodeResult = await currentRecordingGraph.CreateDeviceOutputNodeAsync();
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
            inputNode.AddOutgoingConnection(deviceOutputNode);
            return true;
        }

        private async Task<bool> CreateInputNode()
        {
            // Create a device input node using the default audio input device
            CreateAudioDeviceInputNodeResult deviceInputNodeResult = await currentRecordingGraph.CreateDeviceInputNodeAsync(MediaCategory.Speech,
                AudioEncodingProperties.CreatePcm(44100, 1, 16), SelectedInputDevice);

            if (deviceInputNodeResult.Status != AudioDeviceNodeCreationStatus.Success)
            {
                // Cannot create device input node
                return false;
            }

            deviceInputNode = deviceInputNodeResult.DeviceInputNode;

            return true;
        }

        private async Task<bool> CreateAndConnectFileSaveNode(AudioDeviceInputNode inputNode)
        {
            if (fileOutputNode != null)
            {
                inputNode.RemoveOutgoingConnection(fileOutputNode);
                fileOutputNode.Dispose();
            }

            lastCreatedFileName = $"Recording{recordingCount}.mp3";
            recordingCount++;
            StorageFile file = await storageFolder.CreateFileAsync(lastCreatedFileName, CreationCollisionOption.ReplaceExisting);
            
            MediaEncodingProfile fileProfile = MediaEncodingProfile.CreateMp3(AudioEncodingQuality.High);
            CreateAudioFileOutputNodeResult fileOutputNodeResult = await currentRecordingGraph.CreateFileOutputNodeAsync(file, fileProfile);

            if (fileOutputNodeResult.Status != AudioFileNodeCreationStatus.Success)
            {
                // FileOutputNode creation failed
                return false;
            }
            
            fileOutputNode = fileOutputNodeResult.FileOutputNode;
            inputNode.AddOutgoingConnection(fileOutputNode);
            return true;

        }

        private string GetRecordingFileName()
        {
            return GetRecordingFileName(DateTime.Now);
        }

        private string GetRecordingFileName(DateTime recordingTime)
        {
            return recordingTime.ToString("yyyyMMdd_HH_mm_ss") + ".mp3";
        }

        private void DisposeCurrentRecordingGraph()
        {
            if(currentRecordingGraph != null)
            {
                currentRecordingGraph.Dispose();
                currentRecordingGraph = null;
            }
        }

        private void DisposeCurrentPlaybackGraph()
        {
            if (currentPlaybackGraph != null)
            {
                currentPlaybackGraph.Dispose();
                currentPlaybackGraph = null;
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

        private void OnNewRecordingSaved(string filePath)
        {
            string fileName = Path.GetFileName(filePath);
            if (this.newRecordingSaved != null)
            {
                this.newRecordingSaved(this, new NewRecordingSavedEventArgs(fileName));
            }
        }

        private async Task InitStorage()
        {
            var rootFolder =  Windows.Storage.KnownFolders.MusicLibrary;
            storageFolder = await rootFolder.CreateFolderAsync("RaceCommunicator", CreationCollisionOption.OpenIfExists);
        }
    }

    public class NewRecordingSavedEventArgs : EventArgs
    {
        public string FileName { get; private set; }

        public NewRecordingSavedEventArgs(string fileName)
        {
            this.FileName = fileName;
        }
    }
}
