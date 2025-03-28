using System.Linq;
using HidLibrary;

namespace Teams2HA.API
{
    public static class OnAirWarningLight
    {
        private const int VID = 0x5131;
        private const int PID = 0x2007;

        private static HidDevice _device;

        private static byte[] HID_Write_Data = new byte[25];
        private static bool FirstSend = true;

        public static void Init()
        {
            _device = HidDevices.Enumerate(VID, PID).Any() ? HidDevices.Enumerate(VID, PID).First() : null;
            if (_device == null) { return; }
            _device.OpenDevice();
            _device.Inserted += DeviceAttachedHandler;
        }


        public static void SetCenterColor(byte red, byte green, byte blue)
        {
            HID_Write_Data[1] = HID_Write_Data[10] = HID_Write_Data[13] = HID_Write_Data[22] = green;
            HID_Write_Data[2] = HID_Write_Data[11] = HID_Write_Data[14] = HID_Write_Data[23] = red;
            HID_Write_Data[3] = HID_Write_Data[12] = HID_Write_Data[15] = HID_Write_Data[24] = blue;
            TryConnectAndSendData();
        }

        public static void SetMicrophoneColor(byte red, byte green, byte blue)
        {
            HID_Write_Data[4] = HID_Write_Data[7] = green;
            HID_Write_Data[5] = HID_Write_Data[8] = red;
            HID_Write_Data[6] = HID_Write_Data[9] = blue;
            TryConnectAndSendData();
        }
        public static void SetCameraColor(byte red, byte green, byte blue)
        {
            HID_Write_Data[16] = HID_Write_Data[19] = green;
            HID_Write_Data[17] = HID_Write_Data[20] = red;
            HID_Write_Data[18] = HID_Write_Data[21] = blue;
            TryConnectAndSendData();
        }

        private static void DeviceAttachedHandler()
        {
            TryConnectAndSendData();
        }
        private static void TryConnectAndSendData()
        {
            _device.Write(HID_Write_Data);
            _device.CloseDevice();
        }



        /* reference for how to write data to OnAirWarningLight
            arr[0] = 0x00; //Unknown
            arr[1] = 0x00; //Center1 Green
            arr[2] = 0x00; //Center1 Red
            arr[3] = 0x00; //Center1 Blue
            arr[4] = 0x00; //Mic1 Green
            arr[5] = 0x00; //Mic1 Red
            arr[6] = 0x00; //Mic1 Blue
            arr[7] = 0x00; //Mic2 Green
            arr[8] = 0x00; //Mic2 Red
            arr[9] = 0x00; //Mic2 Blue
            arr[10] = 0x00; //Center2 Green
            arr[11] = 0x00; //Center2 Red
            arr[12] = 0x00; //Center2 Blue
            arr[13] = 0x00; //Center3 Green
            arr[14] = 0x00; //Center3 Red
            arr[15] = 0x00; //Center3 Blue
            arr[16] = 0x00; //Camera1 Green
            arr[17] = 0x00; //Camera1 Red
            arr[18] = 0x00; //Camera1 Blue
            arr[19] = 0x00; //Camera2 Green
            arr[20] = 0x00; //Camera2 Red
            arr[21] = 0x00; //Camera2 Blue
            arr[22] = 0x00; //Center4 Green
            arr[23] = 0x01; //Center4 Red
            arr[24] = 0x01; //Center4 Blue
            */
    }
}