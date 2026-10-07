// Quad Remesher Bridge for Unity
// Port of the Blender bridge by Maxime Rouca (Exoside).
//
// Direct port of QRCheckProgressData from qr_operators.py: the engine reports its progress by
// rewriting a small text file, and the host polls it.
//
// Line 0: progress value    in [0, 1] = computing, 2 = success, < 0 = error
// Line 1: error message (only when the value is negative)
// Line 2: advised sleep time before the next poll, in seconds

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace Exoside.QuadRemesher
{
    /// <summary>Progress values that carry a meaning of their own, as defined by the engine.</summary>
    internal static class QuadRemesherProgressCode
    {
        public const float Success = 2f;
        /// <summary>Engine is showing the EULA or the activation dialog; the user must remesh again.</summary>
        public const float NeedsUserAction = -2f;
        /// <summary>Engine stopped without writing a result.</summary>
        public const float StoppedWithoutResult = -3f;
        /// <summary>Engine returned without ever creating the progress file.</summary>
        public const float ReturnedWithoutProgressFile = -4f;
        /// <summary>No progress file after 40 seconds.</summary>
        public const float NoProgressFile = -10f;
        /// <summary>Progress file exists but could not be parsed (usually a partial write).</summary>
        public const float BadProgressFile = -11f;
    }

    internal readonly struct QuadRemesherProgressStatus
    {
        public readonly float value;
        public readonly string text;
        public readonly float? sleepTimeAdvice;

        public QuadRemesherProgressStatus(float value, string text, float? sleepTimeAdvice)
        {
            this.value = value;
            this.text = text;
            this.sleepTimeAdvice = sleepTimeAdvice;
        }

        public bool isComputing => value >= 0f && value <= 1f;
        public bool isSuccess => Math.Abs(value - QuadRemesherProgressCode.Success) < 0.0001f;
    }

    internal sealed class QuadRemesherProgressReader
    {
        public string progressFilename = "";
        public Process remeshingProcess;

        int m_CheckCounter;
        double m_StartTime;
        bool m_ProgressFileHasBeenCreatedYet;

        public void Reset()
        {
            progressFilename = "";
            remeshingProcess = null;
            m_CheckCounter = 0;
            m_StartTime = 0;
            m_ProgressFileHasBeenCreatedYet = false;
        }

        /// <summary>Called immediately before the engine process is started.</summary>
        public void OnStartRemeshing()
        {
            m_CheckCounter = 0;
            m_StartTime = NowSeconds();
            m_ProgressFileHasBeenCreatedYet = false;
        }

        bool ProcessHasExited()
        {
            try
            {
                return remeshingProcess != null && remeshingProcess.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        public QuadRemesherProgressStatus GetStatus()
        {
            m_CheckCounter++;

            string[] lines;
            try
            {
                // The engine rewrites this file while we read it, so a share-everything read is
                // required; a torn read simply parses as BadProgressFile and we poll again.
                using (var stream = new FileStream(progressFilename, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream))
                {
                    lines = reader.ReadToEnd().Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
                }
            }
            catch (Exception)
            {
                // The progress file does not exist yet, or cannot be opened.
                var elapsed = NowSeconds() - m_StartTime;
                if (!m_ProgressFileHasBeenCreatedYet)
                {
                    if (elapsed < 3)
                        return new QuadRemesherProgressStatus(0f, "", null); // just wait

                    if (elapsed < 40)
                    {
                        if (ProcessHasExited())
                        {
                            return new QuadRemesherProgressStatus(
                                QuadRemesherProgressCode.ReturnedWithoutProgressFile,
                                "Remeshing Failed! (Returned without ProgressFile - 0)", null);
                        }
                        return new QuadRemesherProgressStatus(0f, "", null);
                    }

                    return new QuadRemesherProgressStatus(QuadRemesherProgressCode.NoProgressFile, "", null);
                }

                // Odd case: the file was created earlier but cannot be read now.
                return new QuadRemesherProgressStatus(0f, "", null);
            }

            m_ProgressFileHasBeenCreatedYet = true;

            if (lines.Length >= 1)
            {
                if (!float.TryParse(lines[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
                        out var progressValue))
                {
                    return new QuadRemesherProgressStatus(QuadRemesherProgressCode.BadProgressFile, "", null);
                }

                if (progressValue < 0f)
                {
                    var message = lines.Length >= 2 ? lines[1] : "";
                    return new QuadRemesherProgressStatus(progressValue, message, 0f);
                }

                if (Math.Abs(progressValue - QuadRemesherProgressCode.Success) < 0.0001f)
                    return new QuadRemesherProgressStatus(progressValue, "", 0f);

                // Detect a crash while the last written progress still looks healthy.
                if (m_CheckCounter > 5 && m_CheckCounter % 4 == 0 && ProcessHasExited())
                {
                    return new QuadRemesherProgressStatus(QuadRemesherProgressCode.StoppedWithoutResult,
                        "Remeshing Failed! (Stopped without result)", 0f);
                }

                float? sleepAdvice = null;
                if (lines.Length >= 3 &&
                    float.TryParse(lines[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var advice))
                {
                    sleepAdvice = advice;
                }

                return new QuadRemesherProgressStatus(progressValue, "", sleepAdvice);
            }

            // Empty file: the engine may have died before writing anything.
            if (m_CheckCounter > 8 && m_CheckCounter % 4 == 0 && ProcessHasExited())
            {
                return new QuadRemesherProgressStatus(QuadRemesherProgressCode.ReturnedWithoutProgressFile,
                    "Remeshing Failed! (Returned without ProgressFile)", null);
            }

            return new QuadRemesherProgressStatus(QuadRemesherProgressCode.BadProgressFile,
                "Remeshing Failed! (Bad ProgressFile Data)", null);
        }

        static double NowSeconds()
        {
            return Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        }
    }
}
