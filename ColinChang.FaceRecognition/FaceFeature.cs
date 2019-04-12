﻿using System;
using System.Collections.Generic;
using System.Text;

namespace ColinChang.FaceRecognition
{
    public class Feature
    {
        /// <summary>
        /// Source picture of current feature
        /// </summary>
        public string Source { get; set; }

        public int SerialNo { get; set; }

        public FeaturePosition Position { get; set; }

        public byte[] Content { get; set; }

        public Feature(string source, int serialNo, int x, int y, int width, int height, byte[] content)
        {
            Source = source;
            SerialNo = serialNo;
            Position = new FeaturePosition(x, y, width, height);
            Content = content;
        }
    }

    public struct FeaturePosition
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }

        public FeaturePosition(int x, int y, int width, int height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }
    }
}
