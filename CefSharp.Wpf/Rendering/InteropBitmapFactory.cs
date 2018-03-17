﻿// Copyright © 2010-2017 The CefSharp Authors. All rights reserved.
//
// Use of this source code is governed by a BSD-style license that can be found in the LICENSE file.

using System;
using System.IO.MemoryMappedFiles;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace CefSharp.Wpf.Rendering
{
    /// <summary>
    /// InteropBitmapFactory - creates/updates an InteropBitmap
    /// Uses a MemoryMappedFile for double buffering when the size matches
    /// or creates a new InteropBitmap when required
    /// </summary>
    /// <seealso cref="CefSharp.IBitmapFactory" />
    public class InteropBitmapFactory : IBitmapFactory
    {
        [DllImport("kernel32.dll", EntryPoint = "CopyMemory", SetLastError = false)]
        private static extern void CopyMemory(IntPtr dest, IntPtr src, uint count);

        /// <summary>
        /// The pixel format
        /// </summary>
        private static readonly PixelFormat PixelFormat = PixelFormats.Bgra32;
        private static int BytesPerPixel = PixelFormat.BitsPerPixel / 8;

        private object lockObject = new object();

        private Size viewSize;
        private Size popupSize;
        private DispatcherPriority dispatcherPriority;

        private MemoryMappedFile viewMemoryMappedFile;
        private MemoryMappedFile popupMemoryMappedFile;
        private MemoryMappedViewAccessor viewMemoryMappedViewAccessor;
        private MemoryMappedViewAccessor popupMemoryMappedViewAccessor;

        /// <summary>
        /// Initializes a new instance of the <see cref="InteropBitmapFactory"/> class.
        /// </summary>
        /// <param name="dispatcherPriority">priority at which the bitmap will be updated on the UI thread</param>
        public InteropBitmapFactory(DispatcherPriority dispatcherPriority = DispatcherPriority.Render)
        {
            this.dispatcherPriority = dispatcherPriority;
        }

        public void Dispose()
        {
            ReleaseMemoryMappedView(ref popupMemoryMappedFile, ref popupMemoryMappedViewAccessor);
            ReleaseMemoryMappedView(ref viewMemoryMappedFile, ref viewMemoryMappedViewAccessor);
        }

        void IBitmapFactory.CreateOrUpdateBitmap(bool isPopup, IntPtr buffer, Rect dirtyRect, int width, int height, Image image)
        {
            if (isPopup)
            {
                CreateOrUpdateBitmap(isPopup, buffer, dirtyRect, width, height, image, ref popupSize, ref popupMemoryMappedFile, ref popupMemoryMappedViewAccessor);
            }
            else
            {
                CreateOrUpdateBitmap(isPopup, buffer, dirtyRect, width, height, image, ref viewSize, ref viewMemoryMappedFile, ref viewMemoryMappedViewAccessor);
            }
        }

        private void CreateOrUpdateBitmap(bool isPopup, IntPtr buffer, Rect dirtyRect, int width, int height, Image image, ref Size currentSize, ref MemoryMappedFile mappedFile, ref MemoryMappedViewAccessor viewAccessor)
        {
            if (image.Dispatcher.HasShutdownStarted)
            {
                return;
            }

            var createNewBitmap = false;

            lock (lockObject)
            {
                int pixels = width * height;
                int numberOfBytes = pixels * BytesPerPixel;

                createNewBitmap = mappedFile == null || currentSize.Height != height || currentSize.Width != width;

                if (createNewBitmap)
                {
                    ReleaseMemoryMappedView(ref mappedFile, ref viewAccessor);

                    mappedFile = MemoryMappedFile.CreateNew(null, numberOfBytes, MemoryMappedFileAccess.ReadWrite);

                    viewAccessor = mappedFile.CreateViewAccessor();

                    currentSize.Height = height;
                    currentSize.Width = width;
                }

                //TODO: Performance analysis to determine which is the fastest memory copy function
                //NativeMethodWrapper.CopyMemoryUsingHandle(viewAccessor.SafeMemoryMappedViewHandle.DangerousGetHandle(), buffer, numberOfBytes);
                CopyMemory(viewAccessor.SafeMemoryMappedViewHandle.DangerousGetHandle(), buffer, (uint)numberOfBytes);

                //Take a reference to the backBufferHandle, once we're on the UI thread we need to check if it's still valid
                var backBufferHandle = mappedFile.SafeMemoryMappedFileHandle;

                //Invoke on the WPF UI Thread
                image.Dispatcher.BeginInvoke((Action)(() =>
                {
                    lock (lockObject)
                    {
                        if (backBufferHandle.IsClosed || backBufferHandle.IsInvalid)
                        {
                            return;
                        }

                        if (createNewBitmap)
                        {
                            if (image.Source != null)
                            {
                                image.Source = null;
                                //TODO: Is this still required in newer versions of .Net?
                                GC.Collect(1);
                            }

                            var stride = width * BytesPerPixel;
                            var bitmap = (InteropBitmap)Imaging.CreateBitmapSourceFromMemorySection(backBufferHandle.DangerousGetHandle(), width, height, PixelFormat, stride, 0);
                            image.Source = bitmap;
                        }
                        else
                        {
                            if (image.Source != null)
                            {
                                var sourceRect = new Int32Rect(dirtyRect.X, dirtyRect.Y, dirtyRect.Width, dirtyRect.Height);
                                var bitmap = (InteropBitmap)image.Source;
                                bitmap.Invalidate(sourceRect);
                            }
                        }
                    }
                }), dispatcherPriority);
            }
        }

        private void ReleaseMemoryMappedView(ref MemoryMappedFile mappedFile, ref MemoryMappedViewAccessor stream)
        {
            if (stream != null)
            {
                stream.Dispose();
                stream = null;
            }

            if (mappedFile != null)
            {
                mappedFile.Dispose();
                mappedFile = null;
            }
        }
    }
}
