using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using NAudio.Wave;
using WPF_WhisperSubtitleGenerator.Services;

namespace WPF_WhisperSubtitleGenerator
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private string? _inputFilePath;
        private string? _outputFilePath;
        private string _modelPath;
        private CancellationTokenSource? _cancellationTokenSource;
        private bool _isProcessing;
        private readonly WhisperService _whisperService;

        public MainWindow()
        {
            InitializeComponent();
            
            _whisperService = new WhisperService();
            
            // Load model from Model folder next to executable
            var modelFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Model");
            _modelPath = Path.Combine(modelFolder, "ggml-large-v3.bin");
            
            if (!File.Exists(_modelPath))
            {
                MessageBox.Show($"Whisper model not found at:\n{_modelPath}\n\nPlease place the model file in the Model folder.",
                    "Model Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                StatusText.Text = "Model not found!";
            }
            else
            {
                // Load model in background
                LoadModelAsync();
            }
            
            UpdateGenerateButtonState();
        }

        private async void LoadModelAsync()
        {
            StatusText.Text = "Loading Whisper model... Please wait.";
            Log("🤖 Loading Whisper model in background...");
            
            try
            {
                await _whisperService.LoadModelAsync(_modelPath, "auto");
                
                StatusText.Text = "Model loaded. Ready to process.";
                Log("✅ Whisper model loaded successfully.");
                UpdateGenerateButtonState();
            }
            catch (Exception ex)
            {
                StatusText.Text = "Failed to load model!";
                Log($"❌ Failed to load model: {ex.Message}");
                MessageBox.Show($"Failed to load Whisper model:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BrowseInput_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Media Files|*.mp3;*.wav;*.mp4;*.mkv;*.mov;*.m4a;*.flac;*.ogg;*.aac|All Files|*.*",
                Title = "Select Audio/Video File"
            };

            if (dialog.ShowDialog() == true)
            {
                _inputFilePath = dialog.FileName;
                InputFileTextBox.Text = _inputFilePath;
                
                // Auto-set output path
                var directory = Path.GetDirectoryName(_inputFilePath);
                var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(_inputFilePath);
                _outputFilePath = Path.Combine(directory!, $"{fileNameWithoutExtension}.srt");
                OutputFileTextBox.Text = _outputFilePath;
                
                UpdateGenerateButtonState();
            }
        }

        private void BrowseSavePath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "SRT Subtitle Files|*.srt",
                Title = "Save Subtitle File",
                DefaultExt = ".srt"
            };

            if (!string.IsNullOrEmpty(_inputFilePath))
            {
                dialog.InitialDirectory = Path.GetDirectoryName(_inputFilePath);
                dialog.FileName = Path.GetFileNameWithoutExtension(_inputFilePath) + ".srt";
            }

            if (dialog.ShowDialog() == true)
            {
                _outputFilePath = dialog.FileName;
                OutputFileTextBox.Text = _outputFilePath;
                UpdateGenerateButtonState();
            }
        }

        private void UpdateGenerateButtonState()
        {
            GenerateButton.IsEnabled = !_isProcessing &&
                                       _whisperService.IsModelLoaded &&
                                       !string.IsNullOrEmpty(_inputFilePath) && File.Exists(_inputFilePath) &&
                                       !string.IsNullOrEmpty(_outputFilePath);
        }

        private async void Generate_Click(object sender, RoutedEventArgs e)
        {
            if (_isProcessing)
            {
                _cancellationTokenSource?.Cancel();
                return;
            }

            _isProcessing = true;
            _cancellationTokenSource = new CancellationTokenSource();
            GenerateButton.Content = "⏹ Cancel";
            GenerateButton.IsEnabled = true;
            LogTextBox.Clear();
            ProgressBar.Value = 0;
            ProgressPercentText.Text = "0%";
            CurrentDialogBorder.Visibility = Visibility.Collapsed;
            CurrentDialogText.Text = "";

            string? tempWavFile = null;

            try
            {
                // Step 1: Convert audio to 16kHz WAV
                Log("🔄 Converting audio to 16kHz WAV format...");
                StatusText.Text = "Converting audio format...";
                
                tempWavFile = await Task.Run(() => EnsureWav16kHzAsync(_inputFilePath!));
                Log("✅ Audio conversion complete.");

                // Get audio duration for progress calculation
                TimeSpan audioDuration;
                using (var reader = new AudioFileReader(tempWavFile))
                {
                    audioDuration = reader.TotalTime;
                }
                Log($"📊 Audio duration: {FormatTime(audioDuration)}");

                // Step 2: Process audio with Whisper
                Log("🎯 Starting transcription...");
                StatusText.Text = "Transcribing audio...";

                var subtitles = new List<SubtitleEntry>();
                int segmentIndex = 1;
                var cancellationToken = _cancellationTokenSource.Token;

                await _whisperService.TranscribeAsync(
                    tempWavFile,
                    segment =>
                    {
                        var entry = new SubtitleEntry
                        {
                            Index = segmentIndex++,
                            Start = segment.Start,
                            End = segment.End,
                            Text = segment.Text
                        };
                        subtitles.Add(entry);

                        // Update progress based on current position
                        double progress = Math.Min(100, (segment.End.TotalSeconds / audioDuration.TotalSeconds) * 100);

                        Dispatcher.Invoke(() =>
                        {
                            ProgressBar.Value = progress;
                            ProgressPercentText.Text = $"{progress:F1}%";
                            TimeInfoText.Text = $"Processing: {FormatTime(segment.End)} / {FormatTime(audioDuration)}";
                            StatusText.Text = "Transcribing audio...";

                            // Show current dialog
                            CurrentDialogBorder.Visibility = Visibility.Visible;
                            CurrentDialogText.Text = $"\"{segment.Text}\"";

                            LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] [{FormatSrtTime(segment.Start)} → {FormatSrtTime(segment.End)}]\n   {segment.Text}\n\n");
                            LogTextBox.ScrollToEnd();
                        });
                    },
                    cancellationToken);

                // Step 3: Write SRT file
                Log("📝 Writing SRT file...");
                StatusText.Text = "Writing subtitle file...";

                await WriteSrtFileAsync(_outputFilePath!, subtitles);

                ProgressBar.Value = 100;
                ProgressPercentText.Text = "100%";
                StatusText.Text = "Complete!";
                TimeInfoText.Text = $"Generated {subtitles.Count} subtitle segments";
                CurrentDialogText.Text = "✅ Transcription Complete!";
                Log($"✅ Subtitles saved to: {_outputFilePath}");
                Log($"🎉 Done! {subtitles.Count} segments transcribed.");

                MessageBox.Show($"Subtitles generated successfully!\n\nSaved to: {_outputFilePath}\nSegments: {subtitles.Count}",
                    "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (OperationCanceledException)
            {
                Log("⚠️ Processing cancelled.");
                StatusText.Text = "Cancelled";
                CurrentDialogBorder.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Log($"❌ Error: {ex.Message}");
                StatusText.Text = "Error occurred";
                MessageBox.Show($"An error occurred:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                // Cleanup temp file
                if (tempWavFile != null && tempWavFile != _inputFilePath && File.Exists(tempWavFile))
                {
                    try
                    {
                        File.Delete(tempWavFile);
                        Log("🗑️ Temporary file cleaned up.");
                    }
                    catch { /* Ignore cleanup errors */ }
                }

                _isProcessing = false;
                GenerateButton.Content = "🚀 Generate Subtitles";
                UpdateGenerateButtonState();
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
            }
        }

        private void Log(string message)
        {
            Dispatcher.Invoke(() =>
            {
                LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
                LogTextBox.ScrollToEnd();
            });
        }

        private string EnsureWav16kHzAsync(string inputPath)
        {
            // Check if already 16kHz WAV
            if (Path.GetExtension(inputPath).Equals(".wav", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var reader = new WaveFileReader(inputPath);
                    if (reader.WaveFormat.SampleRate == 16000 && reader.WaveFormat.Channels == 1)
                    {
                        return inputPath;
                    }
                }
                catch { /* Need to convert */ }
            }

            // Convert to 16kHz mono WAV
            string tempPath = Path.Combine(Path.GetTempPath(), $"whisper_temp_{Guid.NewGuid()}.wav");
            
            using (var reader = new AudioFileReader(inputPath))
            {
                // Resample to 16kHz
                var resampler = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(reader, 16000);
                
                int channels = resampler.WaveFormat.Channels;
                var outFormat = new WaveFormat(16000, 16, 1);
                
                using (var waveFileWriter = new WaveFileWriter(tempPath, outFormat))
                {
                    float[] buffer = new float[4096 * channels];
                    int samplesRead;
                    while ((samplesRead = resampler.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        // Convert multi-channel to mono by averaging all channels
                        for (int i = 0; i < samplesRead; i += channels)
                        {
                            float sum = 0;
                            for (int ch = 0; ch < channels && (i + ch) < samplesRead; ch++)
                            {
                                sum += buffer[i + ch];
                            }
                            float monoSample = sum / channels;
                            var sample = (short)(Math.Clamp(monoSample, -1.0f, 1.0f) * short.MaxValue);
                            waveFileWriter.WriteByte((byte)(sample & 0xff));
                            waveFileWriter.WriteByte((byte)((sample >> 8) & 0xff));
                        }
                    }
                }
            }

            return tempPath;
        }

        private static async Task WriteSrtFileAsync(string path, List<SubtitleEntry> subtitles)
        {
            var sb = new StringBuilder();
            
            foreach (var entry in subtitles)
            {
                sb.AppendLine(entry.Index.ToString());
                sb.AppendLine($"{FormatSrtTime(entry.Start)} --> {FormatSrtTime(entry.End)}");
                sb.AppendLine(entry.Text);
                sb.AppendLine();
            }

            await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8);
        }

        private static string FormatSrtTime(TimeSpan time)
        {
            return $"{(int)time.TotalHours:D2}:{time.Minutes:D2}:{time.Seconds:D2},{time.Milliseconds:D3}";
        }

        private static string FormatTime(TimeSpan time)
        {
            if (time.TotalHours >= 1)
                return $"{(int)time.TotalHours}:{time.Minutes:D2}:{time.Seconds:D2}";
            return $"{time.Minutes}:{time.Seconds:D2}";
        }
    }

    public class SubtitleEntry
    {
        public int Index { get; set; }
        public TimeSpan Start { get; set; }
        public TimeSpan End { get; set; }
        public string Text { get; set; } = string.Empty;
    }
}