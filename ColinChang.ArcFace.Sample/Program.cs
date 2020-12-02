﻿using System;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using ColinChang.ArcFace;
using ColinChang.ArcFace.Models;
using Microsoft.Extensions.Configuration;

namespace ColinChang.ArcFace.Sample
{
    internal class Program
    {
        private static async Task Main(string[] args)
        {
            //读取配置
            var config = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();
            var options = new ArcFaceOptions();
            config.Bind(nameof(ArcFaceOptions), options);


            //测试图片
            const string test = "Images/test.jpg";
            const string zys = "Images/zys.jpg";
            const string xy = "Images/xy.jpg";
            const string xy1 = "Images/xy1.jpg";


            using IArcFace arcFace = new ArcFace(options);

            /*
            //获取激活消息
            var info = await arcFace.GetActiveFileInfoAsync();
            // 获取SDK信息
            var version = await arcFace.GetSdkVersionAsync();
            // 人脸检测
            var faces = await arcFace.DetectFaceAsync(test);
            // 获取年龄
            var ages = await arcFace.GetAgeAsync(test);
            // 获取性别
            var genders = await arcFace.GetGenderAsync(test);
            // 获取3D角度
            var face3DAngleInfo = await arcFace.GetFace3DAngleAsync(test);

            // 提取人脸特征
            var features0 = await arcFace.ExtractFaceFeatureAsync(zys);
            var features1 = await arcFace.ExtractFaceFeatureAsync(xy);
            
            // 人脸比对
            var result = await arcFace.CompareFaceFeatureAsync(features0.Data.Single(), features1.Data.Single());
            
            // 活体检测实际应取视频帧图，此处仅作演示
            //RGB活体检测
            var livenessRgb= await arcFace.GetLivenessInfoAsync(Image.FromFile(zys), LivenessMode.RGB);
            //IR活体检测
            var livenessIr= await arcFace.GetLivenessInfoAsync(Image.FromFile(zys), LivenessMode.IR);
            */

            // 初始化人脸库
            await arcFace.InitFaceLibraryAsync(new[] {zys, xy});
            // 搜索人脸库
            var res = await arcFace.SearchFaceAsync(xy1);
            if (res.Code == 0)
                Console.WriteLine("FaceId:{0}\tSimilarity:{1}", res.Data.FaceId, res.Data.Similarity);

            Console.ReadKey();
        }
    }
}