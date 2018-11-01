﻿#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2018 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using ShareX.HelpersLib;
using ShareX.Properties;
using ShareX.ScreenCaptureLib;
using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ShareX
{
    public static class ScreenRecordManager
    {
        public static bool IsRecording { get; private set; }

        private static ScreenRecorder screenRecorder;
        private static ScreenRecordForm recordForm;

        public static void StartStopRecording(ScreenRecordOutput outputType, ScreenRecordStartMethod startMethod, TaskSettings taskSettings)
        {
            if (IsRecording)
            {
                if (recordForm != null && !recordForm.IsDisposed)
                {
                    recordForm.StartStopRecording();
                }
            }
            else
            {
                StartRecording(outputType, taskSettings, startMethod);
            }
        }

        public static void AbortRecording()
        {
            if (IsRecording && recordForm != null && !recordForm.IsDisposed)
            {
                recordForm.AbortRecording();
            }
        }

        private static void StopRecording()
        {
            if (IsRecording && screenRecorder != null)
            {
                screenRecorder.StopRecording();
            }
        }

        private static void StartRecording(ScreenRecordOutput outputType, TaskSettings taskSettings, ScreenRecordStartMethod startMethod = ScreenRecordStartMethod.Region)
        {
            if (outputType == ScreenRecordOutput.GIF)
            {
                taskSettings.CaptureSettings.FFmpegOptions.VideoCodec = FFmpegVideoCodec.gif;
            }

            if (taskSettings.CaptureSettings.FFmpegOptions.IsTwoPassEncodingRequired)
            {
                taskSettings.CaptureSettings.ScreenRecordTwoPassEncoding = true;
            }

            if (!taskSettings.CaptureSettings.ScreenRecordTwoPassEncoding)
            {
                DebugHelper.WriteLine("Starting screen recording. Video encoder: \"{0}\", Audio encoder: \"{1}\", FPS: {2}",
                    taskSettings.CaptureSettings.FFmpegOptions.VideoCodec.GetDescription(), taskSettings.CaptureSettings.FFmpegOptions.AudioCodec.GetDescription(),
                    taskSettings.CaptureSettings.ScreenRecordFPS);
            }
            else
            {
                DebugHelper.WriteLine("Starting screen recording. FPS: {0}", taskSettings.CaptureSettings.GIFFPS);
            }

            if (!TaskHelpers.CheckFFmpeg(taskSettings))
            {
                return;
            }

            if (!taskSettings.CaptureSettings.FFmpegOptions.IsSourceSelected)
            {
                MessageBox.Show(Resources.ScreenRecordForm_StartRecording_FFmpeg_video_and_audio_source_both_can_t_be__None__,
                    "ShareX - " + Resources.ScreenRecordForm_StartRecording_FFmpeg_error, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Rectangle captureRectangle = Rectangle.Empty;

            switch (startMethod)
            {
                case ScreenRecordStartMethod.Region:
                    RegionCaptureTasks.GetRectangleRegion(out captureRectangle, taskSettings.CaptureSettings.SurfaceOptions);
                    break;
                case ScreenRecordStartMethod.ActiveWindow:
                    if (taskSettings.CaptureSettings.CaptureClientArea)
                    {
                        captureRectangle = CaptureHelpers.GetActiveWindowClientRectangle();
                    }
                    else
                    {
                        captureRectangle = CaptureHelpers.GetActiveWindowRectangle();
                    }
                    break;
                case ScreenRecordStartMethod.CustomRegion:
                    captureRectangle = taskSettings.CaptureSettings.CaptureCustomRegion;
                    break;
                case ScreenRecordStartMethod.LastRegion:
                    captureRectangle = Program.Settings.ScreenRecordRegion;
                    break;
            }

            Rectangle screenRectangle = CaptureHelpers.GetScreenBounds();
            captureRectangle = Rectangle.Intersect(captureRectangle, screenRectangle);

            if (taskSettings.CaptureSettings.FFmpegOptions.VideoCodec != FFmpegVideoCodec.gif)
            {
                captureRectangle = CaptureHelpers.EvenRectangleSize(captureRectangle);
            }

            if (IsRecording || !captureRectangle.IsValid() || screenRecorder != null)
            {
                return;
            }

            Program.Settings.ScreenRecordRegion = captureRectangle;

            IsRecording = true;

            string path = "";
            bool abortRequested = false;

            float duration = taskSettings.CaptureSettings.ScreenRecordFixedDuration ? taskSettings.CaptureSettings.ScreenRecordDuration : 0;

            recordForm = new ScreenRecordForm(captureRectangle, taskSettings, startMethod == ScreenRecordStartMethod.Region, duration);
            recordForm.StopRequested += StopRecording;
            recordForm.Show();

            Task.Run(() =>
            {
                try
                {
                    string extension;
                    if (taskSettings.CaptureSettings.ScreenRecordTwoPassEncoding)
                    {
                        extension = "mp4";
                    }
                    else
                    {
                        extension = taskSettings.CaptureSettings.FFmpegOptions.Extension;
                    }
                    string filename = TaskHelpers.GetFilename(taskSettings, extension);
                    path = TaskHelpers.HandleExistsFile(taskSettings.CaptureFolder, filename, taskSettings);

                    if (string.IsNullOrEmpty(path))
                    {
                        abortRequested = true;
                    }

                    if (!abortRequested)
                    {
                        recordForm.ChangeState(ScreenRecordState.BeforeStart);

                        if (taskSettings.CaptureSettings.ScreenRecordAutoStart)
                        {
                            int delay = (int)(taskSettings.CaptureSettings.ScreenRecordStartDelay * 1000);

                            if (delay > 0)
                            {
                                recordForm.InvokeSafe(() => recordForm.StartCountdown(delay));

                                recordForm.RecordResetEvent.WaitOne(delay);
                            }
                        }
                        else
                        {
                            recordForm.RecordResetEvent.WaitOne();
                        }

                        if (recordForm.IsAbortRequested)
                        {
                            abortRequested = true;
                        }

                        if (!abortRequested)
                        {
                            int fps;

                            if (taskSettings.CaptureSettings.FFmpegOptions.VideoCodec == FFmpegVideoCodec.gif)
                            {
                                fps = taskSettings.CaptureSettings.GIFFPS;
                            }
                            else
                            {
                                fps = taskSettings.CaptureSettings.ScreenRecordFPS;
                            }

                            ScreencastOptions options = new ScreencastOptions()
                            {
                                IsRecording = true,
                                IsLossless = taskSettings.CaptureSettings.ScreenRecordTwoPassEncoding,
                                FFmpeg = taskSettings.CaptureSettings.FFmpegOptions,
                                FPS = fps,
                                Duration = duration,
                                OutputPath = path,
                                CaptureArea = captureRectangle,
                                DrawCursor = taskSettings.CaptureSettings.ScreenRecordShowCursor
                            };

                            Screenshot screenshot = TaskHelpers.GetScreenshot(taskSettings);
                            screenshot.CaptureCursor = taskSettings.CaptureSettings.ScreenRecordShowCursor;

                            screenRecorder = new ScreenRecorder(ScreenRecordOutput.FFmpeg, options, screenshot, captureRectangle);
                            screenRecorder.RecordingStarted += () => recordForm.ChangeState(ScreenRecordState.AfterRecordingStart);
                            recordForm.ChangeState(ScreenRecordState.AfterStart);
                            screenRecorder.StartRecording();

                            if (recordForm.IsAbortRequested)
                            {
                                abortRequested = true;
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    DebugHelper.WriteException(e);
                }

                try
                {
                    if (!abortRequested && screenRecorder != null && File.Exists(path))
                    {
                        recordForm.ChangeState(ScreenRecordState.AfterStop);

                        string input = path;

                        if (taskSettings.CaptureSettings.ScreenRecordTwoPassEncoding)
                        {
                            path = Path.Combine(taskSettings.CaptureFolder, TaskHelpers.GetFilename(taskSettings, taskSettings.CaptureSettings.FFmpegOptions.Extension));

                            if (taskSettings.CaptureSettings.FFmpegOptions.VideoCodec == FFmpegVideoCodec.gif)
                            {
                                screenRecorder.FFmpegEncodeAsGIF(input, path, Program.ToolsFolder);
                            }
                            else
                            {
                                screenRecorder.FFmpegEncodeVideo(input, path);
                            }
                        }
                    }
                }
                finally
                {
                    if (recordForm != null)
                    {
                        recordForm.InvokeSafe(() =>
                        {
                            recordForm.Close();
                            recordForm.Dispose();
                            recordForm = null;
                        });
                    }

                    if (screenRecorder != null)
                    {
                        if (taskSettings.CaptureSettings.ScreenRecordTwoPassEncoding && !string.IsNullOrEmpty(screenRecorder.CachePath) && File.Exists(screenRecorder.CachePath))
                        {
                            File.Delete(screenRecorder.CachePath);
                        }

                        screenRecorder.Dispose();
                        screenRecorder = null;

                        if (abortRequested && !string.IsNullOrEmpty(path) && File.Exists(path))
                        {
                            File.Delete(path);
                        }
                    }
                }
            }).ContinueInCurrentContext(() =>
            {
                string customFileName;

                if (!abortRequested && !string.IsNullOrEmpty(path) && File.Exists(path) && TaskHelpers.ShowAfterCaptureForm(taskSettings, out customFileName, null, path))
                {
                    if (!string.IsNullOrEmpty(customFileName))
                    {
                        string currentFilename = Path.GetFileNameWithoutExtension(path);
                        string ext = Path.GetExtension(path);

                        if (!currentFilename.Equals(customFileName, StringComparison.InvariantCultureIgnoreCase))
                        {
                            path = Helpers.RenameFile(path, customFileName + ext);
                        }
                    }

                    WorkerTask task = WorkerTask.CreateFileJobTask(path, taskSettings, customFileName);
                    TaskManager.Start(task);
                }

                abortRequested = false;
                IsRecording = false;
            });
        }
    }
}