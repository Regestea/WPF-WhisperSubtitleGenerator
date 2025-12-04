using System.IO;
using Whisper.net;

namespace WPF_WhisperSubtitleGenerator.Services
{
    /// <summary>
    /// A service to handle audio transcription using Whisper.net.
    /// </summary>
    public class WhisperService
    {
        private WhisperFactory? _factory;
        private WhisperProcessor? _processor;
        private string? _modelPath;
        private bool _isModelLoaded;

        public bool IsModelLoaded => _isModelLoaded;

        /// <summary>
        /// Loads the Whisper model from the specified path and creates the processor.
        /// </summary>
        /// <param name="modelPath">The file path to the Whisper model.</param>
        /// <param name="language">The language code (e.g., "en", "auto").</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <exception cref="FileNotFoundException">Thrown if the model file does not exist.</exception>
        public async Task LoadModelAsync(string modelPath, string language = "auto", CancellationToken cancellationToken = default)
        {
            if (!File.Exists(modelPath))
            {
                throw new FileNotFoundException("Whisper model file not found.", modelPath);
            }

            // Only reload if path changed or not loaded
            if (_factory != null && _modelPath == modelPath && _isModelLoaded)
            {
                return;
            }

            _isModelLoaded = false;
            _modelPath = modelPath;
            
            _factory = await Task.Run(() => WhisperFactory.FromPath(modelPath), cancellationToken);
            
            // Create processor once
            _processor = _factory.CreateBuilder()
                .WithLanguage(language)
                .Build();
                
            _isModelLoaded = true;
        }

        /// <summary>
        /// Transcribes an audio file and returns segments with timestamps.
        /// </summary>
        /// <param name="audioPath">The path to the audio file (must be 16kHz WAV).</param>
        /// <param name="onSegmentTranscribed">Callback for each transcribed segment.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>List of transcribed segments.</returns>
        public async Task<List<TranscriptionSegment>> TranscribeAsync(
            string audioPath,
            Action<TranscriptionSegment>? onSegmentTranscribed = null,
            CancellationToken cancellationToken = default)
        {
            if (!_isModelLoaded || _processor == null)
            {
                throw new InvalidOperationException("Whisper model not loaded. Call LoadModelAsync first.");
            }

            if (!File.Exists(audioPath))
            {
                throw new FileNotFoundException("Audio file not found.", audioPath);
            }

            var segments = new List<TranscriptionSegment>();

            try
            {
                await using var fileStream = File.OpenRead(audioPath);

                await foreach (var result in _processor.ProcessAsync(fileStream, cancellationToken))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    var segment = new TranscriptionSegment
                    {
                        Start = result.Start,
                        End = result.End,
                        Text = result.Text.Trim()
                    };

                    segments.Add(segment);
                    onSegmentTranscribed?.Invoke(segment);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
                throw;
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested)
            {
                // Whisper may throw other exceptions when cancelled (e.g., "Failed to decode during token processing")
                // Treat as cancellation
                throw new OperationCanceledException("Transcription was cancelled.", ex, cancellationToken);
            }

            // Throw if cancelled so caller knows
            cancellationToken.ThrowIfCancellationRequested();

            return segments;
        }
    }

    /// <summary>
    /// Represents a transcribed segment with timestamps.
    /// </summary>
    public class TranscriptionSegment
    {
        public TimeSpan Start { get; set; }
        public TimeSpan End { get; set; }
        public string Text { get; set; } = string.Empty;
    }
}

