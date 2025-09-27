using Microsoft.Data.SqlClient;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Data.SqlTypes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.FaceAnalysis;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

namespace oop_proj
{
    public sealed partial class MainWindow : Window
    {
        private MediaCapture? _mediaCapture;
        private MediaFrameReader? _mediaFrameReader;
        private FaceDetector? _faceDetector;

        private BitmapBounds? _lastDetectedFaceBox;
        private SoftwareBitmap? _lastFrame;

        private SoftwareBitmapSource? _bitmapSource;
        private readonly object _frameLock = new object();

        private SoftwareBitmapSource _previewSource = new();
        //private SoftwareBitmapSource _mirrorSource = new();


        // --- Throttle support ---
        private DateTime _lastDetectionTime = DateTime.MinValue;
        private readonly TimeSpan _detectionInterval = TimeSpan.FromMilliseconds(200);
        Database db;
        public MainWindow()
        {
            this.InitializeComponent();
            this.Title = "Face Capture App (MediaCapture)";

            // Create sources once
            _previewSource = new SoftwareBitmapSource();
            //_mirrorSource = new SoftwareBitmapSource();

            cameraPreview.Source = _previewSource;
            //livePreviewMirror.Source = _mirrorSource;

            InitializeAsync();

            // database
            InitDB();

            AppEvents.OnStatusUpdate += UpdateStatusText;
            this.Closed += async (s, e) => await CleanupCameraAsync();
        }

        private void UpdateStatusText(string text)
        {
            DispatcherQueue.TryEnqueue(() => textStatus.Text = text);
        }

        private void InitDB()
        {
            db = new Database(new SqlConnectionStringBuilder
            {
                DataSource = "(localdb)\\MSSQLLocalDB",
                UserID = "server",
                Password = "pass123",
                InitialCatalog = "oop_system_db"
            });


        }

        private async void InitializeAsync()
        {
            await InitializeFaceDetectorAsync();
            await EnumerateCamerasAsync();
        }


        private async Task InitializeFaceDetectorAsync()
        {
            if (FaceDetector.IsSupported)
            {
                _faceDetector = await FaceDetector.CreateAsync();
            }
            else
            {
                captureButton.IsEnabled = false;
                System.Diagnostics.Debug.WriteLine("Face detection is not supported on this device.");
            }
        }

        private async Task EnumerateCamerasAsync()
        {
            var videoDevices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            cameraNamesForm.ItemsSource = videoDevices;
            cameraNamesForm.DisplayMemberPath = "Name";

            if (videoDevices.Any())
            {
                cameraNamesForm.SelectedIndex = 0;
            }
            else
            {
                captureButton.IsEnabled = false;
                System.Diagnostics.Debug.WriteLine("No cameras found on this device.");
            }
        }

        private async void cameraNamesForm_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cameraNamesForm.SelectedItem is DeviceInformation selectedDevice)
            {
                await InitializeCameraAsync(selectedDevice.Id);
            }
        }

        private async Task InitializeCameraAsync(string deviceId)
        {
            await CleanupCameraAsync();

            try
            {
                _mediaCapture = new MediaCapture();
                await _mediaCapture.InitializeAsync(new MediaCaptureInitializationSettings
                {
                    VideoDeviceId = deviceId,
                    StreamingCaptureMode = StreamingCaptureMode.Video,
                    MemoryPreference = MediaCaptureMemoryPreference.Cpu
                });

                var frameSource = _mediaCapture.FrameSources.Values
                    .FirstOrDefault(source => source.Info.SourceKind == MediaFrameSourceKind.Color);
                if (frameSource == null) return;

                var preferredFormat = frameSource.SupportedFormats
                    .FirstOrDefault(format => format.VideoFormat.Width >= 640 &&
                                              format.Subtype == MediaEncodingSubtypes.Bgra8);
                if (preferredFormat != null)
                {
                    await frameSource.SetFormatAsync(preferredFormat);
                }

                _mediaFrameReader = await _mediaCapture.CreateFrameReaderAsync(frameSource, MediaEncodingSubtypes.Bgra8);
                _mediaFrameReader.FrameArrived += OnFrameArrived;
                await _mediaFrameReader.StartAsync();

                captureButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error initializing camera: {ex.Message}");
                await CleanupCameraAsync();
            }
        }

        private async void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
        {
            using (var frame = sender.TryAcquireLatestFrame())
            {
                if (frame?.VideoMediaFrame?.SoftwareBitmap == null)
                    return;

                // Convert to BGRA8 with premultiplied alpha (required for SoftwareBitmapSource)
                var converted = SoftwareBitmap.Convert(
                    frame.VideoMediaFrame.SoftwareBitmap,
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied);

                // Keep a copy for capture
                lock (_frameLock)
                {
                    _lastFrame?.Dispose();
                    _lastFrame = SoftwareBitmap.Copy(converted);
                }

                if (_faceDetector != null &&
                    (DateTime.Now - _lastDetectionTime) >= _detectionInterval)
                {
                    _lastDetectionTime = DateTime.Now;

                    // Run detection on a copy so we don’t dispose prematurely
                    using (var detectionCopy = SoftwareBitmap.Copy(converted))
                    {
                        var detectedFaces = await _faceDetector.DetectFacesAsync(detectionCopy);

                        // Update UI safely
                        await DispatcherQueue.EnqueueAsync(async () =>
                        {
                            DrawBoundingBoxes(detectedFaces, converted.PixelWidth, converted.PixelHeight);

                            if (_previewSource != null)
                            {
                                try
                                {
                                    var uiCopy = SoftwareBitmap.Convert(
                                        converted,
                                        BitmapPixelFormat.Bgra8,
                                        BitmapAlphaMode.Premultiplied);

                                    // Update existing sources
                                    await _previewSource.SetBitmapAsync(uiCopy);
                                    uiCopy.Dispose();
                                    //await _mirrorSource.SetBitmapAsync(uiCopy);
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine($"SetBitmapAsync failed: {ex.Message}");
                                }
                            }
                        });

                    }
                }

                converted.Dispose();
            }
        }

        private (double scale, double offsetX, double offsetY) GetImageScaleAndOffset(
    int bitmapWidth, int bitmapHeight,
    double controlWidth, double controlHeight)
        {
            double scale = Math.Min(controlWidth / bitmapWidth, controlHeight / bitmapHeight);

            double scaledWidth = bitmapWidth * scale;
            double scaledHeight = bitmapHeight * scale;

            double offsetX = (controlWidth - scaledWidth) / 2.0;
            double offsetY = (controlHeight - scaledHeight) / 2.0;

            return (scale, offsetX, offsetY);
        }


        private void DrawBoundingBoxes(
      System.Collections.Generic.IList<DetectedFace> faces,
      int originalWidth,
      int originalHeight)
        {
            boundingBoxCanvas.Children.Clear();
            _lastDetectedFaceBox = null;

            if (!faces.Any()) return;

            _lastDetectedFaceBox = faces[0].FaceBox;

            var (scale, offsetX, offsetY) = GetImageScaleAndOffset(
      originalWidth, originalHeight,
      boundingBoxCanvas.ActualWidth, boundingBoxCanvas.ActualHeight);

            foreach (var face in faces)
            {
                var box = face.FaceBox;
                var shape = new Microsoft.UI.Xaml.Shapes.Rectangle
                {
                    Stroke = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.DeepSkyBlue),
                    StrokeThickness = 2,
                    Width = box.Width * scale,
                    Height = box.Height * scale
                };

                Canvas.SetLeft(shape, offsetX + box.X * scale);
                Canvas.SetTop(shape, offsetY + box.Y * scale);

                boundingBoxCanvas.Children.Add(shape);
            }
        }


        private async void captureButton_Click(object sender, RoutedEventArgs e)
        {
            SoftwareBitmap? frameCopy = null;
            BitmapBounds? faceBox = null;

            // Safe copy under lock
            lock (_frameLock)
            {
                if (_lastFrame != null && _lastDetectedFaceBox.HasValue)
                {
                    frameCopy = SoftwareBitmap.Copy(_lastFrame);
                    faceBox = _lastDetectedFaceBox;
                }
            }

            if (frameCopy == null || !faceBox.HasValue)
                return;

            using (frameCopy)
            using (var stream = new InMemoryRandomAccessStream())
            {
                BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);

                encoder.SetSoftwareBitmap(frameCopy);

                // Clamp face box to avoid invalid crop
                var safeBounds = ClampBounds(faceBox.Value, frameCopy.PixelWidth, frameCopy.PixelHeight);
                encoder.BitmapTransform.Bounds = safeBounds;

                await encoder.FlushAsync();

                var colorSource = new BitmapImage();
                stream.Seek(0);
                await colorSource.SetSourceAsync(stream);
                capturedFaceImage.Source = colorSource;
            }
        }


        private static BitmapBounds ClampBounds(BitmapBounds box, int bitmapWidth, int bitmapHeight)
        {
            uint x = Math.Min((uint)box.X, (uint)(bitmapWidth - 1));
            uint y = Math.Min((uint)box.Y, (uint)(bitmapHeight - 1));
            uint w = Math.Min((uint)box.Width, (uint)(bitmapWidth - x));
            uint h = Math.Min((uint)box.Height, (uint)(bitmapHeight - y));

            if (w == 0) w = 1;
            if (h == 0) h = 1;

            return new BitmapBounds { X = x, Y = y, Width = w, Height = h };
        }

        private async Task CleanupCameraAsync()
        {
            if (_mediaFrameReader != null)
            {
                _mediaFrameReader.FrameArrived -= OnFrameArrived;
                try { await _mediaFrameReader.StopAsync(); } catch { }
                _mediaFrameReader.Dispose();
                _mediaFrameReader = null;
            }

            if (_mediaCapture != null)
            {
                _mediaCapture.Dispose();
                _mediaCapture = null;
            }

            lock (_frameLock)
            {
                _lastFrame?.Dispose();
                _lastFrame = null;
            }

            if (_bitmapSource != null)
            {
                //cameraPreview.Source = null;
                _bitmapSource = null;
            }
        }
    }
}
