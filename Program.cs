using System.Net.NetworkInformation;
using OpenTK.Audio.OpenAL;
using FFmpeg.AutoGen;
using System.Runtime.InteropServices;
using System.Text;
class RadioApp
{
    private bool isOnline;
    private string recordingsPath = "recordings/";
    private readonly Dictionary<string, string> radioStations;
    private CancellationTokenSource? streamCancellation;
    
    // OpenAL fields
    private ALContext context;
    private ALDevice device;
    private int source;
    private int[] buffers;
    private const int NUM_BUFFERS = 3;
    private bool isPlaying;

    private unsafe AVFormatContext* formatContext;
    private unsafe AVCodecContext* codecContext;
    private int audioStreamIndex;
    private byte[]? audioBuffer;
    private const int AUDIO_BUFFER_SIZE = 192000; 

    private bool isRecording = false;
    private FileStream? recordingStream = null;
    private BinaryWriter? recordingWriter = null;
    private long recordingDataLength = 0;
    private const int WAVE_FORMAT_PCM = 1;

    private readonly object streamLock = new object();

    public RadioApp()
    {
        device = ALC.OpenDevice(null);
        context = ALC.CreateContext(device, new ALContextAttributes());
        ALC.MakeContextCurrent(context);
        source = AL.GenSource();
        buffers = AL.GenBuffers(NUM_BUFFERS);

        radioStations = new Dictionary<string, string>()
        {
            {"SomaFM Groove Salad", "https://ice4.somafm.com/groovesalad-128-mp3"},
            {"SomaFM Drone Zone", "https://ice4.somafm.com/dronezone-128-mp3"},
            {"Radio Paradise Main", "https://stream.radioparadise.com/mp3-128"},
            {"Venice Classic", "https://uk2.streamingpulse.com/ssl/vcr1"},
            {"Chillsky", "http://hyades.shoutca.st:8043/stream"},
            {"LoFi Hip Hop", "http://stream.zeno.fm/f3wvbbqmdg8uv"},
            {"Jazz 24", "https://live.wostreaming.net/direct/ppm-jazz24aac-ibc1"},
            {"Classical Radio", "http://stream.live.vc.bbcmedia.co.uk/bbc_radio_three"},
            {"Classic Rock Florida", "http://us4.internet-radio.com:8258/stream"},
            {"Electronic Culture", "http://radio.electronicculture.ru:8000/mp3-128"}
        };

        Directory.CreateDirectory(recordingsPath);
        InitializeFFmpeg();
        SetupNetworkMonitoring();
    }

    private void SetupNetworkMonitoring()
    {
        NetworkChange.NetworkAvailabilityChanged += NetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged += NetworkAddressChanged;
        
        // Initial check
        CheckInternetConnection();
    }

    private void NetworkAvailabilityChanged(object sender, NetworkAvailabilityEventArgs e)
    {
        CheckInternetConnection();
    }

    private void NetworkAddressChanged(object sender, EventArgs e)
    {
        CheckInternetConnection();
    }

    private bool CheckInternetConnection()
    {
        try
        {
            using (var ping = new Ping())
            {
                var reply = ping.Send("8.8.8.8", 2000);
                bool wasOnline = isOnline;
                isOnline = reply?.Status == IPStatus.Success;
                
                if (wasOnline != isOnline)
                {
                    if (isOnline)
                    {
                        Console.WriteLine("\nApplication is running in Online mode");
                    }
                    else
                    {
                        Console.WriteLine("\nApplication is running in Offline mode");
                        StopPlayback();
                    }
                }
                return isOnline;
            }
        }
        catch
        {
            isOnline = false;
            return false;
        }
    }

    public void ListAvailableContent()
    {
        if (isOnline)
        {
            Console.WriteLine("\nAvailable Radio Stations:");
            var stationList = radioStations.Keys.ToList();
            for (int i = 0; i < stationList.Count; i++)
            {
                Console.WriteLine($"{i + 1}. {stationList[i]}");
            }
        }

        Console.WriteLine("\nRecorded Streams:");
        var recordings = Directory.GetFiles(recordingsPath, "*.wav");
        for (int i = 0; i < recordings.Length; i++)
        {
            int displayIndex = isOnline ? radioStations.Count + i + 1 : i + 1;
            Console.WriteLine($"{displayIndex}. {Path.GetFileName(recordings[i])}");
        }
    }

    public void Play(int selection)
    {
        try
        {
            var recordings = Directory.GetFiles(recordingsPath, "*.wav");
            
            if (selection <= 0)
            {
                Console.WriteLine("Invalid selection.");
                return;
            }

            if (isOnline)
            {
                var stationList = radioStations.Keys.ToList();
                if (selection <= stationList.Count)
                {
                    string stationName = stationList[selection - 1];
                    if (radioStations.TryGetValue(stationName, out string? streamUrl))
                    {
                        if (isPlaying)
                        {
                            StopPlayback();
                            Thread.Sleep(500);
                        }
                        PlayStream(streamUrl, stationName);
                        return;
                    }
                }
                
                int recordingIndex = selection - stationList.Count - 1;
                if (recordingIndex >= 0 && recordingIndex < recordings.Length)
                {
                    if (isPlaying)
                    {
                        StopPlayback();
                        Thread.Sleep(500);
                    }
                    PlayRecordedStream(recordings[recordingIndex]);
                    return;
                }
            }
            else
            {
                int recordingIndex = selection - 1;
                if (recordingIndex >= 0 && recordingIndex < recordings.Length)
                {
                    if (isPlaying)
                    {
                        StopPlayback();
                        Thread.Sleep(500);
                    }
                    PlayRecordedStream(recordings[recordingIndex]);
                    return;
                }
            }

            Console.WriteLine("Invalid selection.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error playing content: {ex.Message}");
        }
    }

    private unsafe void InitializeFFmpeg()
    {
        string libraryPath;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            libraryPath = "/usr/lib";  // Linux path
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            libraryPath = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator)
                .FirstOrDefault(p => File.Exists(Path.Combine(p, "avcodec-60.dll")))
                ?? Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        }
        else
        {
            throw new PlatformNotSupportedException("Unsupported operating system");
        }

        try
        {
            ffmpeg.RootPath = libraryPath;
            Console.WriteLine($"FFmpeg library path set to: {libraryPath}");

            formatContext = ffmpeg.avformat_alloc_context();
            audioBuffer = new byte[AUDIO_BUFFER_SIZE];
            audioStreamIndex = -1;
            
            Console.WriteLine("FFmpeg initialized successfully");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error initializing FFmpeg: {ex.Message}");
            throw;
        }
    }

    private unsafe void PlayStream(string streamUrl, string stationName)
    {
        if (isPlaying)
        {
            Console.WriteLine("Please wait, stream is switching...");
            return;
        }

        AVFormatContext* localFormatContext = null;
        AVCodecContext* localCodecContext = null;

        try
        {
            if (isPlaying)
            {
                StopPlayback();
                Thread.Sleep(1000);
            }

            lock (streamLock)
            {
                streamCancellation = new CancellationTokenSource();
            }
            
            Console.WriteLine($"\nPlaying {stationName}...");

            localFormatContext = ffmpeg.avformat_alloc_context();
            AVFormatContext* formatCtx = null;
            
            int error = ffmpeg.avformat_open_input(&formatCtx, streamUrl, null, null);
            if (error < 0)
            {
                ReportFFmpegError("Could not open stream", error);
                return;
            }
            localFormatContext = formatCtx;

            if (ffmpeg.avformat_find_stream_info(localFormatContext, null) < 0)
            {
                throw new Exception("Could not find stream info");
            }

            audioStreamIndex = -1;
            for (int i = 0; i < localFormatContext->nb_streams; i++)
            {
                if (localFormatContext->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                {
                    audioStreamIndex = i;
                    break;
                }
            }

            if (audioStreamIndex == -1)
            {
                throw new Exception("Could not find audio stream");
            }

            var codecParams = localFormatContext->streams[audioStreamIndex]->codecpar;
            var codec = ffmpeg.avcodec_find_decoder(codecParams->codec_id);
            if (codec == null)
            {
                throw new Exception("Codec not found");
            }

            localCodecContext = ffmpeg.avcodec_alloc_context3(codec);
            if (localCodecContext == null)
            {
                throw new Exception("Could not allocate codec context");
            }

            if (ffmpeg.avcodec_parameters_to_context(localCodecContext, codecParams) < 0)
            {
                throw new Exception("Could not copy codec params to codec context");
            }

            if (ffmpeg.avcodec_open2(localCodecContext, codec, null) < 0)
            {
                throw new Exception("Could not open codec");
            }

            lock (streamLock)
            {
                formatContext = localFormatContext;
                codecContext = localCodecContext;
            }

            Task.Run(() =>
            {
                try
                {
                    AVPacket* packet = ffmpeg.av_packet_alloc();
                    while (!streamCancellation.Token.IsCancellationRequested)
                    {
                        lock (streamLock)
                        {
                            if (formatContext == null) break;

                            error = ffmpeg.av_read_frame(formatContext, packet);
                            if (error >= 0)
                            {
                                if (!streamCancellation.Token.IsCancellationRequested && 
                                    packet->stream_index == audioStreamIndex)
                                {
                                    ProcessPacket(packet);
                                }
                                ffmpeg.av_packet_unref(packet);
                            }
                        }
                        Thread.Sleep(1);
                    }
                    ffmpeg.av_packet_free(&packet);
                }
                catch (Exception ex)
                {
                    if (!streamCancellation.Token.IsCancellationRequested)
                    {
                        Console.WriteLine($"Streaming error: {ex.Message}");
                    }
                }
            }, streamCancellation.Token);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error playing stream: {ex.Message}");
            CleanupResources(localFormatContext, localCodecContext);
        }
    }

    private unsafe void ConvertAudioAndQueue(AVFrame* frame)
    {
        var outputFormat = AVSampleFormat.AV_SAMPLE_FMT_S16;
        var outputChannels = 2;
        var outputSampleRate = 44100;

        var swrContext = ffmpeg.swr_alloc();
        
        var inputLayout = new AVChannelLayout();
        var outputLayout = new AVChannelLayout();

        ffmpeg.av_channel_layout_default(&inputLayout, frame->ch_layout.nb_channels > 0 ? frame->ch_layout.nb_channels : 2);
        ffmpeg.av_channel_layout_default(&outputLayout, 2); 

        int error;
        error = ffmpeg.swr_alloc_set_opts2(
            &swrContext,
            &outputLayout,                          
            outputFormat,                           
            outputSampleRate,                       
            &inputLayout,                           
            (AVSampleFormat)frame->format,          
            frame->sample_rate,                     
            0,                                      
            null);                                  

        if (error < 0)
        {
            ReportFFmpegError("Failed to set resampler options", error);
            return;
        }

        error = ffmpeg.swr_init(swrContext);
        if (error < 0)
        {
            ReportFFmpegError("Failed to initialize resampler", error);
            ffmpeg.swr_free(&swrContext);
            return;
        }

        var outputSamples = ffmpeg.av_rescale_rnd(
            ffmpeg.swr_get_delay(swrContext, frame->sample_rate) + frame->nb_samples,
            outputSampleRate,
            frame->sample_rate,
            AVRounding.AV_ROUND_UP);

        var outputBufferSize = (int)(outputSamples * outputChannels * 2);
        if (audioBuffer == null || audioBuffer.Length < outputBufferSize)
        {
            audioBuffer = new byte[outputBufferSize];
        }

        fixed (byte* outputPtr = audioBuffer)
        {
            byte** outputArray;
            outputArray = (byte**)ffmpeg.av_malloc((ulong)sizeof(byte*));
            outputArray[0] = outputPtr;

            var convertedSamples = ffmpeg.swr_convert(
                swrContext,
                outputArray,
                (int)outputSamples,
                frame->extended_data,
                frame->nb_samples);

            ffmpeg.av_free(outputArray);

            if (convertedSamples > 0)
            {
                var actualSize = convertedSamples * outputChannels * 2;
                QueueAudioBuffer(audioBuffer, actualSize);
            }
        }

        ffmpeg.swr_free(&swrContext);
        ffmpeg.av_channel_layout_uninit(&inputLayout);
        ffmpeg.av_channel_layout_uninit(&outputLayout);
    }

    private unsafe void ReportFFmpegError(string message, int error)
    {
        const int errorBufferSize = 1024;
        var errorBuffer = new byte[errorBufferSize];
        fixed (byte* ptr = errorBuffer)
        {
            ffmpeg.av_strerror(error, ptr, (ulong)errorBufferSize);
            string errorMessage = Marshal.PtrToStringAnsi((IntPtr)ptr) ?? "Unknown error";
            Console.WriteLine($"{message}: {errorMessage}");
        }
    }

    private void QueueAudioBuffer(byte[] audioData, int size)
    {
        var alBuffer = AL.GenBuffer();
        
        unsafe
        {
            fixed (byte* ptr = audioData)
            {
                AL.BufferData(alBuffer, ALFormat.Stereo16, (IntPtr)ptr, size, 44100);
            }
        }

        AL.SourceQueueBuffer(source, alBuffer);

        if (!isPlaying)
        {
            AL.SourcePlay(source);
            isPlaying = true;
        }

        if (isRecording && recordingWriter != null)
        {
            try
            {
                recordingWriter.Write(audioData, 0, size);
                recordingDataLength += size;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error writing to recording: {ex.Message}");
                StopRecording();
            }
        }

        AL.GetSource(source, ALGetSourcei.BuffersProcessed, out int processed);
        while (processed > 0)
        {
            var processedBuffer = AL.SourceUnqueueBuffer(source);
            AL.DeleteBuffer(processedBuffer);
            processed--;
        }
    }

    private void PlayOfflineContent(string selection)
    {
        try
        {
            var recordings = Directory.GetFiles(recordingsPath, "*.wav");
            
            if (recordings.Length == 0)
            {
                Console.WriteLine("No recordings available.");
                return;
            }

            string recordingPath;
            if (int.TryParse(selection, out int index) && index > 0 && index <= recordings.Length)
            {
                recordingPath = recordings[index - 1];
            }
            else
            {
                recordingPath = Path.Combine(recordingsPath, selection);
                if (!File.Exists(recordingPath))
                {
                    Console.WriteLine("Recording not found.");
                    return;
                }
            }

            StopPlayback();
            Thread.Sleep(500); 
            PlayRecordedStream(recordingPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error playing offline content: {ex.Message}");
        }
    }

    private void PlayRecordedStream(string recordingPath)
    {
        try
        {
            Console.WriteLine($"\nPlaying recording: {Path.GetFileName(recordingPath)}");

            using (var fileStream = File.OpenRead(recordingPath))
            using (var reader = new BinaryReader(fileStream))
            {
                var riff = new string(reader.ReadChars(4));
                if (riff != "RIFF")
                {
                    throw new Exception("Invalid WAV file format");
                }

                fileStream.Seek(44, SeekOrigin.Begin);

                const int bufferSize = 4096;
                byte[] buffer = new byte[bufferSize];
                int bytesRead;

                int alBuffer = AL.GenBuffer();

                using (var memoryStream = new MemoryStream())
                {
                    while ((bytesRead = fileStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        memoryStream.Write(buffer, 0, bytesRead);
                    }

                    byte[] audioData = memoryStream.ToArray();
                    
                    unsafe
                    {
                        fixed (byte* ptr = audioData)
                        {
                            AL.BufferData(alBuffer, ALFormat.Stereo16, (IntPtr)ptr, audioData.Length, 44100);
                        }
                    }
                }

                AL.SourceQueueBuffer(source, alBuffer);
                AL.SourcePlay(source);
                isPlaying = true;

                Task.Run(() =>
                {
                    try
                    {
                        while (isPlaying)
                        {
                            AL.GetSource(source, ALGetSourcei.SourceState, out int state);
                            if (state == (int)ALSourceState.Stopped)
                            {
                                CleanupPlayback();
                                Console.WriteLine("\nRecording playback completed.");
                                break;
                            }
                            Thread.Sleep(100);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error monitoring playback: {ex.Message}");
                    }
                });
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error playing recording: {ex.Message}");
            CleanupPlayback();
        }
    }

    private void CleanupPlayback()
    {
        try
        {
            if (isPlaying)
            {
                AL.SourceStop(source);
                
                AL.GetSource(source, ALGetSourcei.BuffersQueued, out int queued);
                while (queued-- > 0)
                {
                    int buffer = AL.SourceUnqueueBuffer(source);
                    AL.DeleteBuffer(buffer);
                }

                isPlaying = false;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during playback cleanup: {ex.Message}");
        }
    }

    public void StopPlayback()
    {
        try
        {
            lock (streamLock)
            {
                if (isRecording)
                {
                    StopRecording();
                }

                if (streamCancellation != null)
                {
                    streamCancellation.Cancel();
                    Thread.Sleep(500);
                    streamCancellation.Dispose();
                    streamCancellation = null;
                }

                if (isPlaying)
                {
                    CleanupPlayback();
                    Console.WriteLine("\nPlayback stopped.");
                }

                unsafe
                {
                    if (codecContext != null)
                    {
                        var tempCodecContext = codecContext;
                        ffmpeg.avcodec_free_context(&tempCodecContext);
                        codecContext = null;
                    }
                    if (formatContext != null)
                    {
                        var tempFormatContext = formatContext;
                        ffmpeg.avformat_close_input(&tempFormatContext);
                        formatContext = null;
                    }
                }

                audioStreamIndex = -1;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error stopping playback: {ex.Message}");
        }
    }

    private unsafe void CleanupResources(AVFormatContext* localFormatContext, AVCodecContext* localCodecContext)
    {
        try
        {
            if (localCodecContext != null)
            {
                ffmpeg.avcodec_free_context(&localCodecContext);
            }
            if (localFormatContext != null)
            {
                ffmpeg.avformat_close_input(&localFormatContext);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during cleanup: {ex.Message}");
        }
    }

    private unsafe void ProcessPacket(AVPacket* packet)
    {
        if (packet->stream_index == audioStreamIndex)
        {
            AVFrame* frame = ffmpeg.av_frame_alloc();
            try
            {
                int response = ffmpeg.avcodec_send_packet(codecContext, packet);
                if (response < 0)
                {
                    ReportFFmpegError("Error sending packet to decoder", response);
                    return;
                }

                while (response >= 0)
                {
                    response = ffmpeg.avcodec_receive_frame(codecContext, frame);
                    if (response == ffmpeg.AVERROR(ffmpeg.EAGAIN) || response == ffmpeg.AVERROR_EOF)
                        break;
                    else if (response < 0)
                    {
                        ReportFFmpegError("Error during decoding", response);
                        break;
                    }

                    if (frame->nb_samples > 0)
                    {
                        ConvertAudioAndQueue(frame);
                    }
                }
            }
            finally
            {
                ffmpeg.av_frame_free(&frame);
            }
        }
    }

    public void StartRecording(string stationName)
    {
        if (isRecording)
        {
            Console.WriteLine("Already recording!");
            return;
        }

        try
        {
            string fileName = Path.Combine(recordingsPath, $"{stationName}_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
            recordingStream = new FileStream(fileName, FileMode.Create);
            recordingWriter = new BinaryWriter(recordingStream);

            // RIFF header
            recordingWriter.Write(Encoding.ASCII.GetBytes("RIFF")); // ChunkID
            recordingWriter.Write(0); // ChunkSize (to be updated)
            recordingWriter.Write(Encoding.ASCII.GetBytes("WAVE")); // Format

            // fmt chunk
            recordingWriter.Write(Encoding.ASCII.GetBytes("fmt ")); // Subchunk1ID
            recordingWriter.Write(16); // Subchunk1Size (16 for PCM)
            recordingWriter.Write((short)WAVE_FORMAT_PCM); // AudioFormat
            recordingWriter.Write((short)2); // NumChannels (stereo)
            recordingWriter.Write(44100); // SampleRate
            recordingWriter.Write(44100 * 2 * 2); // ByteRate
            recordingWriter.Write((short)(2 * 2)); // BlockAlign
            recordingWriter.Write((short)16); // BitsPerSample

            // data chunk
            recordingWriter.Write(Encoding.ASCII.GetBytes("data")); // Subchunk2ID
            recordingWriter.Write(0); // Subchunk2Size 

            recordingDataLength = 0;
            isRecording = true;
            Console.WriteLine($"\nStarted recording to {fileName}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error starting recording: {ex.Message}");
            StopRecording();
        }
    }

    public void StopRecording()
    {
        if (!isRecording)
            return;

        try
        {
            isRecording = false;

            if (recordingWriter != null && recordingStream != null)
            {
                recordingStream.Seek(4, SeekOrigin.Begin);
                recordingWriter.Write((int)(recordingDataLength + 36)); // Update RIFF chunk size

                recordingStream.Seek(40, SeekOrigin.Begin);
                recordingWriter.Write((int)recordingDataLength); // Update data chunk size

                recordingWriter.Close();
                recordingStream.Close();
                recordingWriter = null;
                recordingStream = null;

                Console.WriteLine("\nRecording stopped and saved");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error stopping recording: {ex.Message}");
        }
    }

    public void Dispose()
    {
        NetworkChange.NetworkAvailabilityChanged -= NetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged -= NetworkAddressChanged;
        
        streamCancellation?.Cancel();
        StopPlayback();
        StopRecording();

        unsafe
        {
            var localCodecContext = codecContext;
            if (localCodecContext != null)
            {
                ffmpeg.avcodec_free_context(&localCodecContext);
                codecContext = null;
            }

            var localFormatContext = formatContext;
            if (localFormatContext != null)
            {
                ffmpeg.avformat_close_input(&localFormatContext);
                formatContext = null;
            }
        }

        if (device != IntPtr.Zero)
        {
            AL.DeleteSource(source);
            AL.DeleteBuffers(buffers);
            ALC.DestroyContext(context);
            ALC.CloseDevice(device);
        }
    }

    static void Main(string[] args)
    {
        Console.WriteLine("Radio Application Starting...");
        var radio = new RadioApp();

        while (true)
        {
            Console.WriteLine("\nCommands:");
            Console.WriteLine("1. List available content");
            Console.WriteLine("2. Play");
            Console.WriteLine("3. Stop");
            Console.WriteLine("4. Start Recording");
            Console.WriteLine("5. Stop Recording");
            Console.WriteLine("6. Exit");

            if (int.TryParse(Console.ReadLine(), out int input))
            {
                switch (input)
                {
                    case 1:
                        radio.ListAvailableContent();
                        break;
                    case 2:
                        Console.WriteLine("Enter the number of the content to play:");
                        if (int.TryParse(Console.ReadLine(), out int selection))
                        {
                            radio.Play(selection);
                        }
                        else
                        {
                            Console.WriteLine("Invalid input. Please enter a number.");
                        }
                        break;
                    case 3:
                        radio.StopPlayback();
                        break;
                    case 4:
                        Console.WriteLine("Enter the name of station to record:");
                        radio.StartRecording(Console.ReadLine());
                        break;
                    case 5:
                        radio.StopRecording();
                        break;
                    case 6:
                        radio.Dispose();
                        return;
                    default:
                        Console.WriteLine("Invalid command");
                        break;
                }
            }
            else
            {
                Console.WriteLine("Invalid input. Please enter a number.");
            }
        }
    }
}
