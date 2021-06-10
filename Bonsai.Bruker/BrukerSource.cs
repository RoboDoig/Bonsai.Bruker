using Bonsai;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Disposables;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using PrairieLink;
using OpenCV.Net;

namespace Bonsai.Bruker
{
    [WorkflowElementIcon(typeof(ElementCategory), "ElementIcon.Video")]
    [Description("Bruker imaging input")]
    public class BrukerSource : Source<IplImage>
    {
        int samplesPerPixel;
        int pixelsPerLine;
        int linesPerFrame;
        int totalSamplesPerFrame;
        int numSamplesRead;
        List<short> sampleBuffer = new List<short>();
        IObservable<IplImage> source;
        readonly object captureLock = new object();

        public BrukerSource()
        {
            source = Observable.Create<IplImage>((observer, cancellationToken) =>
            {
                return Task.Factory.StartNew(() =>
                {
                    var prairieLink = new Application();
                    prairieLink.Connect();
                    samplesPerPixel = prairieLink.SamplesPerPixel();
                    pixelsPerLine = prairieLink.PixelsPerLine();
                    linesPerFrame = prairieLink.LinesPerFrame();
                    totalSamplesPerFrame = samplesPerPixel * pixelsPerLine * linesPerFrame;
                    prairieLink.SendScriptCommands("-DoNotWaitForScans");
                    prairieLink.SendScriptCommands("-LimitGSDMABufferSize true 100");
                    prairieLink.SendScriptCommands("-StreamRawData true 50");
                    prairieLink.SendScriptCommands("-fa 1");
                    prairieLink.SendScriptCommands("-lv on");

                    lock (captureLock)
                    {
                        try
                        {
                            while (!cancellationToken.IsCancellationRequested)
                            {
                                // Read samples
                                short[] samples = prairieLink.ReadRawDataStream(ref numSamplesRead);
                                sampleBuffer.AddRange(samples.Take(numSamplesRead));

                                int numWholeFramesGrabbed = sampleBuffer.Count / totalSamplesPerFrame;
                                List<short> toProcess = sampleBuffer.GetRange(0, numWholeFramesGrabbed * totalSamplesPerFrame);
                                sampleBuffer.RemoveRange(0, numWholeFramesGrabbed * totalSamplesPerFrame);

                                if (numWholeFramesGrabbed > 0)
                                {
                                    for (int i = 0; i<numWholeFramesGrabbed; i++)
                                    {
                                        short[,] frame = ProcessFrame(toProcess);
                                        GCHandle pinnedArray = GCHandle.Alloc(frame, GCHandleType.Pinned);
                                        IntPtr pointer = pinnedArray.AddrOfPinnedObject();
                                        observer.OnNext(new IplImage(new Size(512, 512), IplDepth.S16, 1, pointer));
                                    }
                                }
                            }
                        }
                        finally
                        {
                            prairieLink.SendScriptCommands("-lv off");
                            prairieLink.Disconnect();
                        }
                    }
                },
                cancellationToken,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            })
            .PublishReconnectable()
            .RefCount();
        }

        public short[,] ProcessFrame(List<short> inFrame)
        {
            short[,] outFrame = new short[linesPerFrame, pixelsPerLine];
            bool doFlip = true;

            // row loop
            for (int i = 0; i<linesPerFrame; i++)
            {
                doFlip = !doFlip;

                // column loop
                for (int j = 0; j<pixelsPerLine; j++)
                {

                    // sample loop
                    short pixelSum = 0;
                    short pixelCount = 0;
                    for (int k = 0; k < samplesPerPixel; k++)
                    {
                        short sampleValue = inFrame[(i * linesPerFrame * samplesPerPixel) + (j * samplesPerPixel) + k];
                        if (sampleValue >= 0)
                        {
                            pixelSum += sampleValue;
                            pixelCount++;
                        }
                    }

                    if (doFlip)
                    {
                        outFrame[i, pixelsPerLine - 1 - j] = (short)(pixelSum / pixelCount);
                    } else
                    {
                        outFrame[i, j] = (short)(pixelSum / pixelCount);
                    }
                }
            }

            return outFrame;
        }

        public override IObservable<IplImage> Generate()
        {
            return source;
        }
    }
}
