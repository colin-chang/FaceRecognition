﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ColinChang.ArcFace.Models;
using ColinChang.ArcFace.Utils;
using Microsoft.Extensions.Options;

namespace ColinChang.ArcFace
{
    public class ArcFace : IArcFace
    {
        #region 图像质量要求

        /// <summary>
        /// 图像尺寸上限
        /// </summary>
        private const long ASF_MAX_IMAGE_SIZE = 10 * 1024 * 1024;

        /// <summary>
        /// 图像尺寸下线
        /// </summary>
        private const int ASF_MIN_IMAGE_SIZE = 2 * 1024;

        /// <summary>
        /// 支持的图片格式
        /// </summary>
        private readonly string[] _supportedImageExtensions = {".jpg", ".png", ".bmp"};

        #endregion

        #region 引擎池

        private readonly ConcurrentQueue<IntPtr> _imageEngines = new ConcurrentQueue<IntPtr>();
        private readonly int _imageEnginesCount = 0;
        private readonly EventWaitHandle _imageWaitHandle = new AutoResetEvent(true);

        private readonly ConcurrentQueue<IntPtr> _videoEngines = new ConcurrentQueue<IntPtr>();
        private readonly int _videoEnginesCount = 0;
        private readonly EventWaitHandle _videoWaitHandle = new AutoResetEvent(true);

        private readonly ConcurrentQueue<IntPtr> _rgbEngines = new ConcurrentQueue<IntPtr>();
        private readonly int _rgbEnginesCount = 0;
        private readonly EventWaitHandle _rgbWaitHandle = new AutoResetEvent(true);

        private readonly ConcurrentQueue<IntPtr> _irEngines = new ConcurrentQueue<IntPtr>();
        private readonly int _irEnginesCount = 0;
        private readonly EventWaitHandle _irWaitHandle = new AutoResetEvent(true);

        #endregion

        /// <summary>
        /// 人脸库
        /// </summary>
        private readonly ConcurrentDictionary<string, IntPtr> _faceLibrary = new ConcurrentDictionary<string, IntPtr>();

        private readonly ArcFaceOptions _options;

        public ArcFace(IOptions<ArcFaceOptions> options) : this(options.Value)
        {
        }

        public ArcFace(ArcFaceOptions options)
        {
            _options = options;
            OnlineActive();
        }

        #region SDK信息 激活信息/版本信息

        public async Task<OperationResult<ActiveFileInfo>> GetActiveFileInfoAsync() =>
            await Task.Run(() =>
            {
                var pointer = IntPtr.Zero;
                try
                {
                    pointer = Marshal.AllocHGlobal(Marshal.SizeOf<AsfActiveFileInfo>());
                    var code = AsfHelper.ASFGetActiveFileInfo(pointer);
                    if (code != 0)
                        return new OperationResult<ActiveFileInfo>(code);

                    var info = Marshal.PtrToStructure<AsfActiveFileInfo>(pointer);
                    return new OperationResult<ActiveFileInfo>(info.Cast());
                }
                finally
                {
                    if (pointer != IntPtr.Zero)
                        Marshal.FreeHGlobal(pointer);
                }
            });

        public async Task<VersionInfo> GetSdkVersionAsync() =>
            await Task.Run(() =>
            {
                var pointer = IntPtr.Zero;
                try
                {
                    pointer = Marshal.AllocHGlobal(Marshal.SizeOf<AsfVersionInfo>());
                    AsfHelper.ASFGetVersion(pointer);
                    var version = Marshal.PtrToStructure<AsfVersionInfo>(pointer);
                    return version.Cast();
                }
                finally
                {
                    Marshal.FreeHGlobal(pointer);
                }
            });

        #endregion

        #region 人脸属性 3D角度/年龄/性别

        public async Task<OperationResult<Face3DAngle>> GetFace3DAngleAsync(string image) =>
            await ProcessImageAsync<AsfFace3DAngle, Face3DAngle>(image, FaceHelper.GetFace3DAngleAsync);

        public async Task<OperationResult<AgeInfo>> GetAgeAsync(string image) =>
            await ProcessImageAsync<AsfAgeInfo, AgeInfo>(image, FaceHelper.GetAgeAsync);

        public async Task<OperationResult<GenderInfo>> GetGenderAsync(string image) =>
            await ProcessImageAsync<AsfGenderInfo, GenderInfo>(image, FaceHelper.GetGenderAsync);

        #endregion

        #region 核心功能 人脸检测/特征提取/人脸比对

        public async Task<OperationResult<MultiFaceInfo>> DetectFaceAsync(string image) =>
            await ProcessImageAsync<AsfMultiFaceInfo, MultiFaceInfo>(image, FaceHelper.DetectFaceAsync);

        public async Task<OperationResult<LivenessInfo>> GetLivenessInfoAsync(Image image, LivenessMode mode)
        {
            if (mode == LivenessMode.RGB)
                return await ProcessImageAsync<AsfLivenessInfo, LivenessInfo>(image, FaceHelper.GetRgbLivenessInfoAsync,
                    DetectionModeEnum.RGB);

            return await ProcessImageAsync<AsfLivenessInfo, LivenessInfo>(image, FaceHelper.GetIrLivenessInfoAsync,
                DetectionModeEnum.IR);
        }

        public async Task<OperationResult<IEnumerable<byte[]>>> ExtractFaceFeatureAsync(string image) =>
            await ProcessImageAsync<IEnumerable<byte[]>, IEnumerable<byte[]>>(image,
                FaceHelper.ExtractFeatureAsync);

        public async Task<OperationResult<float>> CompareFaceFeatureAsync(byte[] feature1, byte[] feature2) =>
            await Task.Run(() =>
            {
                var engine = IntPtr.Zero;
                var featureA = IntPtr.Zero;
                var featureB = IntPtr.Zero;
                try
                {
                    engine = GetEngine(DetectionModeEnum.Image);
                    featureA = feature1.ToFaceFeature();
                    featureB = feature2.ToFaceFeature();

                    var similarity = 0f;
                    var code = AsfHelper.ASFFaceFeatureCompare(engine, featureA, featureB, ref similarity);
                    return new OperationResult<float>(similarity, code);
                }
                finally
                {
                    RecycleEngine(engine, DetectionModeEnum.Image);
                    if (featureA != IntPtr.Zero)
                        Marshal.FreeHGlobal(featureA);
                    if (featureB != IntPtr.Zero)
                        Marshal.FreeHGlobal(featureB);
                }
            });

        #endregion

        #region 人脸库管理 初始化/新增人脸/删除人脸/搜索人脸

        public async Task InitFaceLibraryAsync(IEnumerable<string> images)
        {
            if (images == null || !images.Any())
                return;

            var engine = IntPtr.Zero;
            try
            {
                engine = GetEngine(DetectionModeEnum.Image);
                foreach (var image in images)
                    try
                    {
                        using var img = VerifyImage(image);
                        var faceId = Path.GetFileNameWithoutExtension(image);
                        var feature = await FaceHelper.ExtractSingleFeatureAsync(engine, img);
                        if (feature.Code != 0)
                            continue;

                        _faceLibrary[faceId] = feature.Data;
                    }
                    catch
                    {
                        // ignored
                    }
            }
            finally
            {
                RecycleEngine(engine, DetectionModeEnum.Image);
            }
        }

        public async Task InitFaceLibraryAsync(Dictionary<string, byte[]> faceFeatures) =>
            await Task.Run(() =>
            {
                if (faceFeatures == null || !faceFeatures.Any())
                    return;

                foreach (var (faceId, feature) in faceFeatures)
                    _faceLibrary[faceId] = feature.ToFaceFeature();
            });

        public async Task<long> AddFaceAsync(string image)
        {
            Image img = null;
            var engine = IntPtr.Zero;
            try
            {
                img = VerifyImage(image);
                engine = GetEngine(DetectionModeEnum.Image);
                var faceId = Path.GetFileNameWithoutExtension(image);
                var feature = await FaceHelper.ExtractSingleFeatureAsync(engine, img);
                if (feature.Code == 0)
                    _faceLibrary[faceId] = feature.Data;

                return feature.Code;
            }
            finally
            {
                img?.Dispose();
                RecycleEngine(engine, DetectionModeEnum.Image);
            }
        }

        public async Task AddFaceAsync(string faceId, byte[] feature) =>
            await Task.Run(() =>
            {
                if (string.IsNullOrWhiteSpace(faceId) || feature == null || !feature.Any())
                    return;

                _faceLibrary[faceId] = feature.ToFaceFeature();
            });

        public async Task RemoveFaceAsync(string faceId) =>
            await Task.Run(() =>
            {
                if (string.IsNullOrWhiteSpace(faceId) || !_faceLibrary.ContainsKey(faceId))
                    return;

                _faceLibrary.Remove(faceId, out var feature);
                Marshal.FreeHGlobal(feature);
            });

        public async Task<OperationResult<Recognition>> SearchFaceAsync(string image)
        {
            Image img = null;
            var engine = IntPtr.Zero;
            var featureInfo = IntPtr.Zero;
            try
            {
                img = VerifyImage(image);
                engine = GetEngine(DetectionModeEnum.Image);
                var faceFeature = await FaceHelper.ExtractSingleFeatureAsync(engine, img);
                featureInfo = faceFeature.Data;
                if (faceFeature.Code != 0)
                    return new OperationResult<Recognition>(faceFeature.Code);

                var recognition = new Recognition();
                foreach (var (faceId, feature) in _faceLibrary)
                {
                    var similarity = 0f;
                    var code = AsfHelper.ASFFaceFeatureCompare(engine, featureInfo, feature, ref similarity);
                    if (code != 0)
                        continue;
                    if (similarity <= recognition.Similarity)
                        continue;

                    recognition.Similarity = similarity;
                    recognition.FaceId = faceId;
                }

                return recognition.Similarity < _options.MinSimilarity
                    ? null
                    : new OperationResult<Recognition>(recognition);
            }
            finally
            {
                img?.Dispose();
                if (featureInfo != IntPtr.Zero)
                    Marshal.FreeHGlobal(featureInfo);
                RecycleEngine(engine, DetectionModeEnum.Image);
            }
        }

        public async Task<OperationResult<Recognition>> SearchFaceAsync(byte[] feature) =>
            await Task.Run(() =>
            {
                var engine = IntPtr.Zero;
                var featureInfo = IntPtr.Zero;
                try
                {
                    featureInfo = feature.ToFaceFeature();
                    var recognition = new Recognition();
                    engine = GetEngine(DetectionModeEnum.Image);
                    foreach (var (faceId, faceFeature) in _faceLibrary)
                    {
                        var similarity = 0f;
                        var code = AsfHelper.ASFFaceFeatureCompare(engine, featureInfo, faceFeature, ref similarity);
                        if (code != 0)
                            continue;
                        if (similarity <= recognition.Similarity)
                            continue;

                        recognition.Similarity = similarity;
                        recognition.FaceId = faceId;
                    }

                    return recognition.Similarity < _options.MinSimilarity
                        ? null
                        : new OperationResult<Recognition>(recognition);
                }
                finally
                {
                    if (featureInfo != IntPtr.Zero)
                        Marshal.FreeHGlobal(featureInfo);
                    RecycleEngine(engine, DetectionModeEnum.Image);
                }
            });

        #endregion

        #region 资源管理 激活/引擎池管理/资源回收

        /// <summary>
        /// 在线激活
        /// </summary>
        /// <exception cref="Exception"></exception>
        private void OnlineActive()
        {
            string sdkKey;
            var platform = Environment.OSVersion.Platform;
            var is64Bit = Environment.Is64BitProcess;
            if (platform == PlatformID.Win32NT)
                sdkKey = is64Bit ? _options.SdkKeys.Winx64 : _options.SdkKeys.Winx86;
            else if (platform == PlatformID.Unix && is64Bit)
                sdkKey = _options.SdkKeys.Linux64;
            else
                throw new NotSupportedException("only Windows(x86/x64) and Linux(x64) are supported");

            var code = AsfHelper.ASFOnlineActivation(_options.AppId, sdkKey);
            if (code != 90114)
                throw new Exception($"failed to active. error code:{code}");
        }

        /// <summary>
        /// 从引擎池中获取引擎
        /// </summary>
        /// <param name="mode"></param>
        /// <returns></returns>
        private IntPtr GetEngine(DetectionModeEnum mode)
        {
            IntPtr engine;
            var (engines, enginesCount, waitHandle) = GetEngineStuff(mode);

            //引擎池中有可用引擎则直接返回
            if (engines.TryDequeue(out engine))
                return engine;

            //无可用引擎时需要等待 
            waitHandle.WaitOne();
            if (enginesCount >= _options.MaxSingleTypeEngineCount)
                return GetEngine(mode);

            //引擎池未满则可以直接创建
            engine = InitEngine(mode);
            enginesCount++;
            if (enginesCount < _options.MaxSingleTypeEngineCount)
                waitHandle.Set();
            return engine;
        }

        /// <summary>
        /// 初始化引擎
        /// </summary>
        /// <param name="mode"></param>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        private IntPtr InitEngine(DetectionModeEnum mode)
        {
            var engine = IntPtr.Zero;
            var code = mode switch
            {
                DetectionModeEnum.Image => AsfHelper.ASFInitEngine(
                    AsfDetectionMode.ASF_DETECT_MODE_IMAGE, _options.ImageDetectFaceOrientPriority,
                    _options.ImageDetectFaceScaleVal, _options.MaxDetectFaceNum,
                    FaceEngineMask.ASF_FACE_DETECT | FaceEngineMask.ASF_FACERECOGNITION | FaceEngineMask.ASF_AGE |
                    FaceEngineMask.ASF_GENDER | FaceEngineMask.ASF_FACE3DANGLE, ref engine),
                DetectionModeEnum.Video => AsfHelper.ASFInitEngine(
                    AsfDetectionMode.ASF_DETECT_MODE_VIDEO, _options.VideoDetectFaceOrientPriority,
                    _options.VideoDetectFaceScaleVal, _options.MaxDetectFaceNum,
                    FaceEngineMask.ASF_FACE_DETECT | FaceEngineMask.ASF_FACERECOGNITION, ref engine),
                DetectionModeEnum.RGB => AsfHelper.ASFInitEngine(
                    AsfDetectionMode.ASF_DETECT_MODE_IMAGE, _options.ImageDetectFaceOrientPriority,
                    _options.VideoDetectFaceScaleVal, 1,
                    FaceEngineMask.ASF_FACE_DETECT | FaceEngineMask.ASF_FACERECOGNITION | FaceEngineMask.ASF_LIVENESS,
                    ref engine),
                DetectionModeEnum.IR => AsfHelper.ASFInitEngine(
                    AsfDetectionMode.ASF_DETECT_MODE_IMAGE, _options.ImageDetectFaceOrientPriority,
                    _options.VideoDetectFaceScaleVal, 1,
                    FaceEngineMask.ASF_FACE_DETECT | FaceEngineMask.ASF_FACERECOGNITION |
                    FaceEngineMask.ASF_IR_LIVENESS, ref engine),
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
            };

            if (code != 0)
                throw new Exception($"failed to init engine. error code {code}");
            return engine;
        }

        /// <summary>
        /// 回收引擎
        /// </summary>
        /// <param name="engine"></param>
        /// <param name="mode"></param>
        private void RecycleEngine(IntPtr engine, DetectionModeEnum mode)
        {
            if (engine == IntPtr.Zero)
                return;

            var (engines, enginesCount, waitHandle) = GetEngineStuff(mode);
            engines.Enqueue(engine);
            //尽当引擎池已满时 回收后才需要开门，未满时在创建引擎后就立即开门
            if (enginesCount >= _options.MaxSingleTypeEngineCount)
                waitHandle.Set();
        }

        /// <summary>
        /// 销毁引擎
        /// </summary>
        /// <param name="engines"></param>
        private void UninitEngine(ConcurrentQueue<IntPtr> engines) =>
            ThreadPool.QueueUserWorkItem(state =>
            {
                while (!engines.IsEmpty)
                {
                    if (!engines.TryDequeue(out var engine))
                        Thread.Sleep(1000);

                    var code = AsfHelper.ASFUninitEngine(engine);
                    if (code != 0)
                        engines.Enqueue(engine);
                }
            });

        public void Dispose()
        {
            //释放 WaitHandle
            _imageWaitHandle.Close();
            _imageWaitHandle.Dispose();
            _videoWaitHandle.Close();
            _videoWaitHandle.Dispose();

            //销毁所有引擎
            UninitEngine(_imageEngines);
            UninitEngine(_videoEngines);
            UninitEngine(_rgbEngines);
            UninitEngine(_irEngines);

            //释放 人脸库资源
            foreach (var feature in _faceLibrary.Values)
                Marshal.FreeHGlobal(feature);
        }

        #endregion

        #region 工具方法

        private (ConcurrentQueue<IntPtr> Engines, int EnginesCount, EventWaitHandle EnginesWaitHandle) GetEngineStuff(
            DetectionModeEnum mode) =>
            mode switch
            {
                DetectionModeEnum.Image => (_imageEngines, _imageEnginesCount, _imageWaitHandle),
                DetectionModeEnum.Video => (_videoEngines, _videoEnginesCount, _videoWaitHandle),
                DetectionModeEnum.RGB => (_rgbEngines, _rgbEnginesCount, _rgbWaitHandle),
                DetectionModeEnum.IR => (_irEngines, _irEnginesCount, _irWaitHandle),
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "invalid detection mode")
            };

        private async Task<OperationResult<TK>> ProcessImageAsync<T, TK>(string image,
            Func<IntPtr, Image, Task<OperationResult<T>>> process, DetectionModeEnum mode = DetectionModeEnum.Image)
        {
            var img = VerifyImage(image);
            return await ProcessImageAsync<T, TK>(img, process, mode);
        }

        private async Task<OperationResult<TK>> ProcessImageAsync<T, TK>(Image image,
            Func<IntPtr, Image, Task<OperationResult<T>>> process, DetectionModeEnum mode = DetectionModeEnum.Image)
        {
            var engine = IntPtr.Zero;
            try
            {
                engine = GetEngine(mode);
                return (await process(engine, image)).Cast<TK>();
            }
            finally
            {
                image?.Dispose();
                RecycleEngine(engine, mode);
            }
        }

        private Image VerifyImage(string image)
        {
            if (!File.Exists(image))
                throw new FileNotFoundException($"{image} doesn't exist.");

            var size = new FileInfo(image).Length;
            if (size > ASF_MAX_IMAGE_SIZE)
                throw new Exception($"image is oversize than {ASF_MAX_IMAGE_SIZE}B.");
            if (size < ASF_MIN_IMAGE_SIZE)
                throw new Exception($"image is too small than {ASF_MIN_IMAGE_SIZE}B");

            if (!_supportedImageExtensions.Contains(Path.GetExtension(image).ToLower()))
                throw new Exception("unsupported image type.");


            try
            {
                var img = Image.FromFile(image);
                if (img == null)
                    throw new InvalidDataException($"invalid image {image}");

                //缩放
                if (img.Width > 1536 || img.Height > 1536)
                    img = ImageHelper.ScaleImage(img, 1536, 1536);
                if (img.Width % 4 != 0)
                    img = ImageHelper.ScaleImage(img, img.Width - img.Width % 4, img.Height);
                return img;
            }
            catch
            {
                throw new Exception("unsupported image type.");
            }
        }

        #endregion
    }
}