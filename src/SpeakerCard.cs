using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;

namespace SoundcoreDesk
{
    /// <summary>
    /// The home-page card of one connected speaker. The Controller fills it in and handles its buttons;
    /// every control acts only on this card's audio endpoint and this speaker's Bluetooth address.
    /// </summary>
    public sealed class SpeakerCard
    {
        static readonly string[] ControlNames = { "VolumeDown", "VolumeUp", "Preset25", "Preset50", "Preset75", "Mute" };
        readonly FrameworkElement view;

        public FrameworkElement View { get { return view; } }
        public Slider Slider { get; private set; }
        /// <summary>The audio endpoint this card controls; replaced on every refresh with the latest snapshot row.</summary>
        public AudioDevice Endpoint { get; set; }
        /// <summary>The Bluetooth device behind the endpoint (matched by container), or null if Windows does not say.</summary>
        public BluetoothDevice Physical { get; set; }
        /// <summary>True while code moves the slider, so the move is not sent back as a volume change.</summary>
        public bool Updating { get; set; }
        public string Label { get { return Physical != null ? Suffix(Physical.Address) : Endpoint.Name; } }

        public SpeakerCard(AudioDevice endpoint)
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SoundcoreDesk.SpeakerCard.xaml"))
                view = (FrameworkElement)XamlReader.Load(stream);
            Endpoint = endpoint;
            Slider = Find<Slider>("VolumeSlider");
        }

        public T Find<T>(string name) where T : class { return (T)view.FindName(name); }
        public void Text(string name, string value) { Find<TextBlock>(name).Text = value; }
        public void OnClick(string name, Action action) { Find<Button>(name).Click += delegate { action(); }; }
        public void SetControls(bool enabled)
        {
            Slider.IsEnabled = enabled;
            foreach (string name in ControlNames) Find<Button>(name).IsEnabled = enabled;
        }
        /// <summary>Gives the volume controls names that say which speaker they belong to.</summary>
        public void NameControls()
        {
            AutomationProperties.SetName(Slider, "喇叭音量 · " + Label);
            AutomationProperties.SetName(Find<Button>("Mute"), "靜音 · " + Label);
        }
        /// <summary>Last two bytes of a Bluetooth address, e.g. "D2:BB": enough to tell identical names apart.</summary>
        public static string Suffix(string address) { return address != null && address.Length == 17 ? address.Substring(12) : address; }
    }
}
