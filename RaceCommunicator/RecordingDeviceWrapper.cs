using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;

namespace RaceCommunicator
{
    public class AudioDeviceWrapper
    {
        private DeviceInformation audioDevice;
        private static readonly AudioDeviceWrapper nullDeviceWrapper = new AudioDeviceWrapper(null);

        public static AudioDeviceWrapper NullDevice()
        {
            return nullDeviceWrapper;
        }

        public AudioDeviceWrapper(DeviceInformation device)
        {
            this.audioDevice = device;
        }

        public override string ToString()
        {
            if (audioDevice != null)
                return $"{audioDevice.Name}";
            else
                return "Select a recording device to start monitoring";
        }

        public DeviceInformation WindowsDeviceInformation
        {
            get { return this.audioDevice; }
        }
    }
}
