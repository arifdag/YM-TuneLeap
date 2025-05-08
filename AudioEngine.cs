using System;
using System.IO;
using System.Linq;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Collections.Generic; // List<T> için
using SoundFingerprinting;         // Ana SoundFingerprinting namespace'i
using SoundFingerprinting.Audio;   // IAudioService, AudioSamples vs. için
using SoundFingerprinting.Audio.NAudio; // NAudioAudioService için
using SoundFingerprinting.Builder; // FingerprintCommandBuilder için
using SoundFingerprinting.Data;    // HashedFingerprint, AVHashes için
using SoundFingerprinting.Configuration; // DefaultFingerprintConfiguration için
using WaveFormat = NAudio.Wave.WaveFormat; // NAudio.Wave.WaveFormat ile çakışmayı önler

namespace CoreAudioProcessing // Class Library'nizin namespace'i
{
    public class AudioEngine : IDisposable
    {
        private WaveInEvent? _waveIn; // Nullable referans tipi
        private MemoryStream? _recordedAudioStream;
        private WaveFileWriter? _writer; // Kaydedilen sesi geçici olarak tutmak için
        private bool _isRecording = false;

        public WaveFormat RecordingFormat { get; set; } = new WaveFormat(44100, 16, 1); // Varsayılan: 44.1kHz, 16-bit, Mono

        // --- Task 1.1.1: Mikrofondan ses kaydı almayı sağlayan fonksiyonlar ---

        public void StartRecording()
        {
            if (_isRecording)
            {
                Console.WriteLine("Zaten kayıt yapılıyor.");
                return;
            }

            _recordedAudioStream = new MemoryStream();
            _waveIn = new WaveInEvent
            {
                WaveFormat = this.RecordingFormat
            };

            _writer = new WaveFileWriter(_recordedAudioStream, _waveIn.WaveFormat);

            _waveIn.DataAvailable += (sender, args) =>
            {
                if (_writer != null)
                {
                    _writer.Write(args.Buffer, 0, args.BytesRecorded);
                }
            };

            _waveIn.RecordingStopped += (sender, args) =>
            {
                _writer?.Dispose(); // WaveFileWriter'ı düzgünce kapat
                _writer = null;
                _waveIn?.Dispose();
                _waveIn = null;
                _isRecording = false;
                Console.WriteLine("Kayıt durduruldu.");
            };

            try
            {
                _waveIn.StartRecording();
                _isRecording = true;
                Console.WriteLine("Kayıt başlatıldı...");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Kayıt başlatılamadı: {ex.Message}");
                CleanUpRecordingResources();
            }
        }

        public void StopRecording()
        {
            if (_waveIn != null && _isRecording)
            {
                _waveIn.StopRecording(); // Bu RecordingStopped event'ini tetikleyecektir
            }
            else
            {
                Console.WriteLine("Kayıt aktif değil.");
            }
        }

        public byte[]? GetRecordedAudioBytes()
        {
            if (_isRecording)
            {
                Console.WriteLine("Lütfen önce kaydı durdurun.");
                return null;
            }
            return _recordedAudioStream?.ToArray();
        }

        // --- Task 1.1.2: Kaydedilen sesin normalizasyonunu sağlayacak fonksiyon ---
        /// <summary>
        /// Verilen ses verisini normalize eder (genellikle -1.0 ile 1.0 arasına veya hedef bir değere).
        /// Bu örnek, peak normalizasyon yapar.
        /// </summary>
        /// <param name="audioData">Normalize edilecek ham ses verisi (WAV formatında byte dizisi).</param>
        /// <param name="targetPeak">Hedeflenen maksimum tepe değeri (örn: 0.95f, %95 normalizasyon).</param>
        /// <returns>Normalize edilmiş ses verisi (WAV formatında byte dizisi).</returns>
        public byte[]? NormalizeAudio(byte[] audioData, float targetPeak = 0.95f)
        {
            if (audioData == null || audioData.Length == 0) return null;

            try
            {
                // Create a MemoryStream from the input audio data.
                // This stream will be used for both passes: peak detection and normalization.
                using (var audioMemoryStream = new MemoryStream(audioData))
                {
                    float maxSampleValue = 0;

                    // First pass: Read all samples to find the peak value.
                    // A new WaveFileReader is created for this pass.
                    using (var readerForPeakDetection = new WaveFileReader(audioMemoryStream))
                    {
                        ISampleProvider sampleProviderForPeak = readerForPeakDetection.ToSampleProvider();
                        
                        // Buffer to hold all samples. Be mindful of very large files,
                        // though for typical recordings this should be acceptable.
                        // readerForPeakDetection.SampleCount might be long, ensure it fits int for array size.
                        if (readerForPeakDetection.SampleCount > int.MaxValue) {
                            Console.WriteLine("Ses dosyası normalizasyon için çok büyük.");
                            return audioData; // Or handle differently
                        }
                        float[] sampleBuffer = new float[readerForPeakDetection.SampleCount];
                        int samplesRead = sampleProviderForPeak.Read(sampleBuffer, 0, (int)readerForPeakDetection.SampleCount);

                        if (samplesRead == 0)
                        {
                            return audioData; // No audio data or empty
                        }

                        for (int i = 0; i < samplesRead; i++)
                        {
                            var absSample = Math.Abs(sampleBuffer[i]);
                            if (absSample > maxSampleValue)
                            {
                                maxSampleValue = absSample;
                            }
                        }
                    } // readerForPeakDetection is disposed here. audioMemoryStream is still open but its position is at the end.

                    // If already normalized or silent, return original data.
                    if (maxSampleValue == 0 || maxSampleValue <= targetPeak)
                    {
                        return audioData;
                    }

                    // Calculate the amplification factor.
                    float amplificationFactor = targetPeak / maxSampleValue;

                    // Reset the MemoryStream's position to the beginning for the second pass.
                    audioMemoryStream.Position = 0;

                    // Second pass: Apply amplification.
                    // A new WaveFileReader is created for this pass, reading from the reset stream.
                    using (var readerForNormalization = new WaveFileReader(audioMemoryStream))
                    {
                        ISampleProvider sampleProviderForNormalization = readerForNormalization.ToSampleProvider();
                        var volumeSampleProvider = new VolumeSampleProvider(sampleProviderForNormalization)
                        {
                            Volume = amplificationFactor
                        };

                        // Write the normalized audio to a new MemoryStream.
                        using (var outStream = new MemoryStream())
                        using (var waveWriter = new WaveFileWriter(outStream, volumeSampleProvider.WaveFormat)) // Use WaveFormat from volumeSampleProvider
                        {
                            // Buffer for writing.
                            byte[] writeBuffer = new byte[volumeSampleProvider.WaveFormat.AverageBytesPerSecond > 0 ? volumeSampleProvider.WaveFormat.AverageBytesPerSecond : 16384]; // Use a reasonable buffer size
                            int bytesRead;
                            
                            // Convert samples back to byte format for WAV.
                            // SampleToWaveProvider16 is suitable for 16-bit PCM.
                            // Ensure the source (volumeSampleProvider) is compatible or handle other bit depths.
                            // DefaultFingerprintConfiguration usually implies 16-bit.
                            var convertingProvider = new SampleToWaveProvider16(volumeSampleProvider);

                            while ((bytesRead = convertingProvider.Read(writeBuffer, 0, writeBuffer.Length)) > 0)
                            {
                                waveWriter.Write(writeBuffer, 0, bytesRead);
                            }
                            waveWriter.Flush();
                            return outStream.ToArray();
                        }
                    }
                } // audioMemoryStream is disposed here.
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Normalizasyon hatası: {ex.Message}");
                return null; // Or rethrow, or return original audioData
            }
        }


        // --- Task 1.1.3: Kaydedilen sesin örnekleme frekansını ayarlayacak fonksiyon ---
        /// <summary>
        /// Verilen ses verisinin örnekleme frekansını hedeflenen frekansa dönüştürür.
        /// </summary>
        /// <param name="audioData">Yeniden örneklenecek ham ses verisi (WAV formatında byte dizisi).</param>
        /// <param name="targetSampleRate">Hedeflenen örnekleme frekansı (örn: 16000 Hz).</param>
        /// <param name="targetChannels">Hedeflenen kanal sayısı (örn: 1 mono için).</param>
        /// <returns>Yeniden örneklenmiş ses verisi (WAV formatında byte dizisi).</returns>
        public byte[]? ResampleAudio(byte[] audioData, int targetSampleRate, int targetChannels = 1)
        {
            if (audioData == null || audioData.Length == 0) return null;

            try
            {
                using (var reader = new WaveFileReader(new MemoryStream(audioData)))
                {
                    if (reader.WaveFormat.SampleRate == targetSampleRate && reader.WaveFormat.Channels == targetChannels && reader.WaveFormat.Encoding == WaveFormatEncoding.Pcm) // Also check encoding if important
                    {
                        // If bits per sample also needs to match, add: && reader.WaveFormat.BitsPerSample == targetBitsPerSample
                        return audioData; // Zaten istenen formatta
                    }

                    // Ensure the target format specifies PCM, as MediaFoundationResampler typically expects/produces PCM.
                    var outFormat = new WaveFormat(targetSampleRate, reader.WaveFormat.BitsPerSample, targetChannels); // Retain original BitsPerSample
                    
                    // Forcing 16-bit if needed:
                    // var outFormat = new WaveFormat(targetSampleRate, 16, targetChannels);


                    using (var resampler = new MediaFoundationResampler(reader, outFormat))
                    {
                        // resampler.ResamplerQuality = 60; // En yüksek kalite (0-60)
                        using (var outStream = new MemoryStream())
                        {
                            WaveFileWriter.WriteWavFileToStream(outStream, resampler);
                            return outStream.ToArray();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Yeniden örnekleme hatası: {ex.Message}");
                return null;
            }
        }

        // --- Task 1.2.2 & 1.2.3: Akustik Parmak İzi Çıkarma ve Geçici Saklama ---
        /// <summary>
        /// Verilen ses verisinden akustik parmak izlerini çıkarır.
        /// NAudioAudioService kullanılarak ses verisi AudioSamples'a dönüştürülürken,
        /// SoundFingerprinting için gereken örnekleme frekansına (örn: 5512 Hz) ve mono kanala çevrilir.
        /// </summary>
        /// <param name="audioBytes">Parmak izi çıkarılacak ses verisi (WAV formatında).</param>
        /// <returns>Oluşturulan parmak izlerinin bir listesi veya hata durumunda null.</returns>
        public async Task<List<HashedFingerprint>?> ExtractFingerprintsAsync(byte[] audioBytes)
        {
            if (audioBytes == null || audioBytes.Length == 0)
            {
                Console.WriteLine("Parmak izi çıkarmak için ses verisi sağlanmadı.");
                return null;
            }

            try
            {
                NAudioService audioService = new NAudioService();
                int fingerprintSampleRate = new DefaultFingerprintConfiguration().SampleRate;

                AudioSamples? audioSamples;
                using (var stream = new MemoryStream(audioBytes))
                using (var waveReader = new WaveFileReader(stream)) 
                {
                    audioSamples = audioService.ReadMonoSamplesFromFile(waveReader.ToString(), fingerprintSampleRate, 0, 0);
                }

                if (audioSamples == null || audioSamples.Samples.Length == 0)
                {
                    Console.WriteLine("Ses verisinden örnekler okunamadı veya ses çok kısa/sessiz.");
                    return new List<HashedFingerprint>();
                }

                var avHashes = await FingerprintCommandBuilder.Instance
                                        .BuildFingerprintCommand()
                                        .From(audioSamples)
                                        .WithFingerprintConfig(config => {
                                            // Example: config.Audio.Stride = new IncrementalStaticStride(1024, 512);
                                            // Example: config.Audio.MaxSamplesPerFingerprint = 8192 * 3; // increase length of individual fingerprints
                                            // config.Audio.SilenceDetection = true; // requires proper VAD setup or can lead to no fingerprints
                                            return config;
                                        })
                                        .Hash(); 

                if (avHashes.Audio.Any())
                {
                    Console.WriteLine($"{avHashes.Audio.Count} adet parmak izi (hash) oluşturuldu.");
                    return avHashes.Audio.ToList();
                }
                else
                {
                    Console.WriteLine("Ses dosyasından parmak izi çıkarılamadı veya ses çok kısa/sessiz.");
                    return new List<HashedFingerprint>(); 
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Parmak izi çıkarma hatası: {ex.ToString()}");
                return null;
            }
        }
        
        private void CleanUpRecordingResources()
        {
            _writer?.Dispose();
            _writer = null;
            _waveIn?.Dispose();
            _waveIn = null;
            _recordedAudioStream?.Dispose();
            _recordedAudioStream = null;
            _isRecording = false;
        }

        public void Dispose()
        {
            CleanUpRecordingResources();
            GC.SuppressFinalize(this);
        }
    }
}