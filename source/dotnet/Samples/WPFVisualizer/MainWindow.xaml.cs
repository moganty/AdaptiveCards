// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.
using AdaptiveCards;
using AdaptiveCards.Rendering;
using AdaptiveCards.Rendering.Wpf;
using Microsoft.Win32;
using Newtonsoft.Json;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Speech.Synthesis;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Newtonsoft.Json.Linq;
using Xceed.Wpf.Toolkit.PropertyGrid;
using Xceed.Wpf.Toolkit.PropertyGrid.Attributes;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Document;
using Jurassic;
using System.Dynamic;
using Windows.Security.ExchangeActiveSyncProvisioning;

namespace WpfVisualizer
{
    public partial class MainWindow : Window
    {
        private bool _dirty;
        private readonly SpeechSynthesizer _synth;
        private DocumentLine _errorLine;
        private ScriptEngine _scriptEngine;

        public MainWindow()
        {
            foreach (var type in typeof(AdaptiveHostConfig).Assembly.GetExportedTypes()
                .Where(t => t.Namespace == typeof(AdaptiveHostConfig).Namespace))
                TypeDescriptor.AddAttributes(type, new ExpandableObjectAttribute());

            InitializeComponent();

            InitializeDataPayload();
            InitializeScriptEngine();

            SwitchState(States.Connect);

            LoadJsonSyntaxHighlighting();

            _synth = new SpeechSynthesizer();
            _synth.SelectVoiceByHints(VoiceGender.Female, VoiceAge.Adult);
            _synth.SetOutputToDefaultAudioDevice();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += Timer_Tick;
            timer.Start();

            int hostConfigIndex = 0;
            int webChatIndex = -1;
            foreach (var config in Directory.GetFiles("HostConfigs", "*.json"))
            {
                hostConfigs.Items.Add(new ComboBoxItem
                {
                    Content = Path.GetFileNameWithoutExtension(config),
                    Tag = config
                });

                if (config.Contains("webchat"))
                {
                    webChatIndex = hostConfigIndex;
                }

                hostConfigIndex++;
            }


            Renderer = new AdaptiveCardRenderer()
            {
                Resources = Resources
            };

            Renderer.FeatureRegistration.Set("acTest", "1.0");

            // Use the Xceed rich input controls
            Renderer.UseXceedElementRenderers();

            // Register custom elements and actions
            // TODO: Change to instance property? Change to UWP parser registration
            AdaptiveTypedElementConverter.RegisterTypedElement<MyCustomRating>();
            AdaptiveTypedElementConverter.RegisterTypedElement<MyCustomAction>();

            Renderer.ElementRenderers.Set<MyCustomRating>(MyCustomRating.Render);

            // This seems unecessary?
            Renderer.ActionHandlers.AddSupportedAction<MyCustomAction>();

            if (webChatIndex != -1)
            {
                hostConfigs.SelectedIndex = webChatIndex;
            }
        }

        private void InitializeDataPayload()
        {
            var clientDeviceInfo = new EasClientDeviceInformation();

            dynamic data = new ExpandoObject();
            data.platform = "WPF";
            data.manufacturer = clientDeviceInfo.SystemManufacturer;
            data.model = clientDeviceInfo.SystemProductName;
            data.osVersion = Environment.OSVersion.Version.ToString();

            DataPayload = JsonConvert.SerializeObject(data, Formatting.Indented);
            textBoxDataPayload.Text = DataPayload;
        }

        private void InitializeScriptEngine()
        {
            _scriptEngine = new ScriptEngine();
            _scriptEngine.ExecuteFile("Scripts\\templateengine.js");
        }

        private enum States
        {
            Connect,
            Connecting,
            Connected
        }

        private States? _currState;
        private void SwitchState(States state)
        {
            if (_currState != null && _currState.Value == state)
            {
                return;
            }

            _currState = state;

            ConnectView.Visibility = Visibility.Collapsed;
            ConnectingView.Visibility = Visibility.Collapsed;
            ConnectedView.Visibility = Visibility.Collapsed;

            switch (state)
            {
                case States.Connect:
                    ConnectView.Visibility = Visibility.Visible;
                    TextBoxConnectCode.SelectAll();
                    TextBoxConnectCode.Focus();
                    break;

                case States.Connecting:
                    ConnectingView.Visibility = Visibility.Visible;
                    break;

                case States.Connected:
                    ConnectedView.Visibility = Visibility.Visible;
                    break;
            }
        }

        private void LoadJsonSyntaxHighlighting()
        {
            using (var xmlReader = new System.Xml.XmlTextReader("SyntaxHighlighting\\JSON.xml"))
            {
                textBox.SyntaxHighlighting = HighlightingLoader.Load(xmlReader, HighlightingManager.Instance);
            }
            using (var xmlReader = new System.Xml.XmlTextReader("SyntaxHighlighting\\JSON.xml"))
            {
                textBoxDataPayload.SyntaxHighlighting = HighlightingLoader.Load(xmlReader, HighlightingManager.Instance);
            }
        }

        public AdaptiveCardRenderer Renderer { get; set; }

        private void Timer_Tick(object sender, EventArgs e)
        {
            if (_dirty)
            {
                _dirty = false;
                RenderCard();
            }
        }

        public string CardPayload
        {
            get { return textBox.Text; }
            set { textBox.Text = value; }
        }

        public string DataPayload { get; private set; }

        public string ConnectionError
        {
            get { return (string)GetValue(ConnectionErrorProperty); }
            set { SetValue(ConnectionErrorProperty, value); }
        }

        // Using a DependencyProperty as the backing store for ConnectionError.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty ConnectionErrorProperty =
            DependencyProperty.Register("ConnectionError", typeof(string), typeof(MainWindow), new PropertyMetadata(null));

        private void RenderCard()
        {
            cardError.Children.Clear();
            cardGrid.Opacity = 0.65;

            string transformedCard = CardPayload;
            try
            {
                _scriptEngine.SetGlobalValue("cardJson", CardPayload);
                _scriptEngine.SetGlobalValue("dataJson", DataPayload);
                transformedCard = _scriptEngine.Evaluate<string>("TemplateEngine.transform(cardJson, dataJson)");
            }
            catch { }

            try
            {

                AdaptiveCardParseResult parseResult = AdaptiveCard.FromJson(transformedCard);

                AdaptiveCard card = parseResult.Card;

                /*
                // Example on how to override the Action Positive and Destructive styles
                Style positiveStyle = new Style(typeof(Button));
                positiveStyle.Setters.Add(new Setter(Button.BackgroundProperty, Brushes.Green));
                Style otherStyle = new Style(typeof(Button));
                otherStyle.Setters.Add(new Setter(Button.BackgroundProperty, Brushes.Yellow));
                otherStyle.Setters.Add(new Setter(Button.ForegroundProperty, Brushes.Red));

                Renderer.Resources.Add("Adaptive.Action.Submit.positive", positiveStyle);
                Renderer.Resources.Add("Adaptive.Action.Submit.other", otherStyle);
                */

                RenderedAdaptiveCard renderedCard = Renderer.RenderCard(card);
                // TODO: should we have an option to render fallback card instead of exception?

                // Wire up click handler
                renderedCard.OnAction += OnAction;

                renderedCard.OnMediaClicked += OnMediaClick;

                cardGrid.Opacity = 1;
                cardGrid.Children.Clear();
                cardGrid.Children.Add(renderedCard.FrameworkElement);

                // Report any warnings
                var allWarnings = parseResult.Warnings.Union(renderedCard.Warnings);
                foreach (var warning in allWarnings)
                {
                    ShowWarning(warning.Message);
                }
            }
            catch (AdaptiveRenderException ex)
            {
                var fallbackCard = new TextBlock
                {
                    Text = ex.CardFallbackText ?? "Sorry, we couldn't render the card"
                };

                cardGrid.Children.Add(fallbackCard);
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
        }

        private void OnAction(RenderedAdaptiveCard sender, AdaptiveActionEventArgs e)
        {
            if (e.Action is AdaptiveOpenUrlAction openUrlAction)
            {
                Process.Start(openUrlAction.Url.AbsoluteUri);
            }
            else if (e.Action is AdaptiveShowCardAction showCardAction)
            {
                // Action.ShowCard can be rendered inline automatically, or in "popup" mode
                // If the Host Config is set to Popup mode, then the app needs to show it
                if (Renderer.HostConfig.Actions.ShowCard.ActionMode == ShowCardActionMode.Popup)
                {
                    var dialog = new ShowCardWindow(showCardAction.Title, showCardAction, Resources);
                    dialog.Owner = this;
                    dialog.ShowDialog();
                }
            }
            else if (e.Action is AdaptiveSubmitAction submitAction)
            {
                var inputs = sender.UserInputs.AsJson();

                // Merge the Action.Submit Data property with the inputs
                inputs.Merge(submitAction.Data);

                MessageBox.Show(this, JsonConvert.SerializeObject(inputs, Formatting.Indented), "SubmitAction");
            }
        }

        private void OnMediaClick(RenderedAdaptiveCard sender, AdaptiveMediaEventArgs e)
        {
            MessageBox.Show(this, JsonConvert.SerializeObject(e.Media), "Host received a Media");
        }

        private void ShowWarning(string message)
        {
            // Ignore these
            if (message.Contains("'$when'"))
            {
                return;
            }

            var textBlock = new TextBlock
            {
                Text = "WARNING: " + message,
                TextWrapping = TextWrapping.Wrap,
                Style = Resources["Warning"] as Style
            };
            var button = new Button { Content = textBlock };
            cardError.Children.Add(button);
        }

        private void ShowError(Exception err)
        {
            var textBlock = new TextBlock
            {
                Text = "ERROR: " + err.Message,
                TextWrapping = TextWrapping.Wrap,
                Style = Resources["Error"] as Style
            };
            var button = new Button { Content = textBlock };
            button.Click += Button_Click;
            cardError.Children.Add(button);

            var iPos = err.Message.IndexOf("line ");
            if (iPos > 0)
            {
                iPos += 5;
                var iEnd = err.Message.IndexOf(",", iPos);

                var line = 1;
                if (int.TryParse(err.Message.Substring(iPos, iEnd - iPos), out line))
                {
                    if (line == 0) line = 1;
                    iPos = err.Message.IndexOf("position ");
                    if (iPos > 0)
                    {
                        iPos += 9;
                        iEnd = err.Message.IndexOf(".", iPos);
                        var position = 0;
                        if (int.TryParse(err.Message.Substring(iPos, iEnd - iPos), out position))
                            _errorLine = textBox.Document.GetLineByNumber(Math.Min(line, textBox.Document.LineCount));
                    }
                }
            }
        }

        private void _OnMissingInput(object sender, MissingInputEventArgs args)
        {
            MessageBox.Show("Required input is missing.");
            args.FrameworkElement.Focus();
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            if (_errorLine != null)
                textBox.Select(_errorLine.Offset, _errorLine.Length);
        }

        private void loadButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog();
            dlg.DefaultExt = ".json";
            dlg.Filter = "Json documents (*.json)|*.json";
            var result = dlg.ShowDialog();
            if (result == true)
            {
                CardPayload = File.ReadAllText(dlg.FileName).Replace("\t", "  ");
                _dirty = true;
            }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            var binding = new CommandBinding(NavigationCommands.GoToPage, GoToPage, CanGoToPage);
            // Register CommandBinding for all windows.
            CommandManager.RegisterClassCommandBinding(typeof(Window), binding);
        }


        private void GoToPage(object sender, ExecutedRoutedEventArgs e)
        {
            if (e.Parameter is string)
            {
                var name = e.Parameter as string;
                if (!string.IsNullOrWhiteSpace(name))
                    Process.Start(name);
            }
        }

        private void CanGoToPage(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = true;
        }

        private async void viewImage_Click(object sender, RoutedEventArgs e)
        {
            var supportsInteractivity = Renderer.HostConfig.SupportsInteractivity;

            try
            {
                this.IsEnabled = false;

                //Disable interactivity to remove inputs and actions from the image
                Renderer.HostConfig.SupportsInteractivity = false;

                var renderedCard = await Renderer.RenderCardToImageAsync(AdaptiveCard.FromJson(CardPayload).Card, false);
                using (var imageStream = renderedCard.ImageStream)
                {
                    new ViewImageWindow(renderedCard.ImageStream).Show();
                }
            }
            catch
            {
                MessageBox.Show("Failed to render image");
            }
            finally
            {
                Renderer.HostConfig.SupportsInteractivity = supportsInteractivity;
                this.IsEnabled = true;
            }
        }

        private void speak_Click(object sender, RoutedEventArgs e)
        {
            var result = AdaptiveCard.FromJson(CardPayload);
            var card = result.Card;

            _synth.SpeakAsyncCancelAll();
            if (card.Speak != null)
            {
                _synth.SpeakSsmlAsync(FixSSML(card.Speak));
            }
        }

        private string FixSSML(string speak)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<speak version=\"1.0\"");
            sb.AppendLine(" xmlns =\"http://www.w3.org/2001/10/synthesis\"");
            sb.AppendLine(" xml:lang=\"en-US\">");
            sb.AppendLine(speak);
            sb.AppendLine("</speak>");
            return sb.ToString();
        }

        private void textBox_TextChanged(object sender, EventArgs e)
        {
            //_dirty = true;
            RenderCard();
        }

        private void toggleOptions_Click(object sender, RoutedEventArgs e)
        {
            hostConfigEditor.Visibility = hostConfigEditor.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        }

        public AdaptiveHostConfig HostConfig
        {
            get => Renderer.HostConfig;
            set
            {
                hostConfigerror.Children.Clear();
                Renderer.HostConfig = value;
                _dirty = true;
                if (value != null)
                {
                    var props = value.GetType()
                        .GetRuntimeProperties()
                        .Where(p => typeof(AdaptiveConfigBase).IsAssignableFrom(p.PropertyType));

                    foreach (var x in value.AdditionalData)
                    {
                        var textBlock = new TextBlock
                        {
                            Text = $"Unknown property {x.Key}",
                            TextWrapping = TextWrapping.Wrap,
                            Style = Resources["Warning"] as Style
                        };
                        hostConfigerror.Children.Add(textBlock);
                    }
                }
            }
        }

        private void hostConfigs_Selected(object sender, RoutedEventArgs e)
        {
            HostConfig = AdaptiveHostConfig.FromJson(File.ReadAllText((string)((ComboBoxItem)hostConfigs.SelectedItem).Tag));
            hostConfigEditor.SelectedObject = Renderer.HostConfig;
        }

        private void loadConfig_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog();
            dlg.DefaultExt = ".json";
            dlg.Filter = "Json documents (*.json)|*.json";
            var result = dlg.ShowDialog();
            if (result == true)
            {
                HostConfig = AdaptiveHostConfig.FromJson(File.ReadAllText(dlg.FileName));
            }
        }

        private void saveConfig_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog();
            dlg.DefaultExt = ".json";
            dlg.Filter = "Json documents (*.json)|*.json";
            var result = dlg.ShowDialog();
            if (result == true)
            {
                var json = JsonConvert.SerializeObject(Renderer.HostConfig, Formatting.Indented);
                File.WriteAllText(dlg.FileName, json);
            }
        }

        private void HostConfigEditor_OnPropertyValueChanged(object sender, PropertyValueChangedEventArgs e)
        {
            _dirty = true;
        }

        private void TextBoxConnectCode_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Connect();
            }
        }

        private void ButtonConnect_Click(object sender, RoutedEventArgs e)
        {
            Connect();
        }

        private HostConnection _hostConnection;
        private void Connect()
        {
            SwitchState(States.Connecting);

            string hostId = TextBoxConnectCode.Text.Trim();

            _hostConnection = new HostConnection(hostId);
            _hostConnection.OnConnected += _hostConnection_OnConnected;
            _hostConnection.OnCardJsonReceived += _hostConnection_OnCardJsonReceived;
            _hostConnection.OnClosed += _hostConnection_OnClosed;
            _hostConnection.OnError += _hostConnection_OnError;
            _hostConnection.OnReconnecting += _hostConnection_OnReconnecting;
            _hostConnection.OnFailed += _hostConnection_OnFailed;
            _hostConnection.StartConnect();
        }

        private void _hostConnection_OnFailed(object sender, EventArgs e)
        {
            DestroyHostConnection();
            Dispatcher.Invoke(delegate
            {
                FailedConnectError.Visibility = Visibility.Visible;
                SwitchState(States.Connect);
            });
        }

        private void _hostConnection_OnReconnecting(object sender, EventArgs e)
        {
            Dispatcher.Invoke(delegate
            {
                ConnectionError = "Reconnecting...";
            });
        }

        private void _hostConnection_OnError(object sender, string error)
        {
            Dispatcher.Invoke(delegate
            {
                ConnectionError = error;
            });
        }

        private void ClearError()
        {
            ConnectionError = null;
        }

        private void _hostConnection_OnClosed(object sender, EventArgs e)
        {
            DestroyHostConnection();
            Dispatcher.Invoke(delegate
            {
                SwitchState(States.Connect);
            });
        }

        private void _hostConnection_OnCardJsonReceived(object sender, string cardJson)
        {
            Dispatcher.Invoke(delegate
            {
                ClearError();
                CardPayload = cardJson;
            });
        }

        private void _hostConnection_OnConnected(object sender, EventArgs e)
        {
            Dispatcher.Invoke(delegate
            {
                ClearError();
                SwitchState(States.Connected);
            });
        }

        private void DestroyHostConnection()
        {
            _hostConnection.OnError -= _hostConnection_OnError;
            _hostConnection.OnConnected -= _hostConnection_OnConnected;
            _hostConnection.OnClosed -= _hostConnection_OnClosed;
            _hostConnection.OnCardJsonReceived -= _hostConnection_OnCardJsonReceived;
            _hostConnection.OnReconnecting -= _hostConnection_OnReconnecting;
            _hostConnection.OnFailed -= _hostConnection_OnFailed;
            _hostConnection = null;
        }
    }
}
