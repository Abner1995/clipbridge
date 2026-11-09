// 与老 App GlobalUsing.cs 对齐（双存期共享代码习惯）；App 引用替换为 NewApp 命名空间
global using System;
global using System.Collections.Generic;
global using System.Net.Http;
global using System.Text;
global using System.Text.Json;
global using System.Threading;
global using System.Threading.Tasks;
global using System.Security.Cryptography;
global using System.IO;
global using System.Collections.Concurrent;
global using System.Collections.ObjectModel;
global using System.Linq;
global using System.Text.RegularExpressions;

global using CommunityToolkit.Mvvm.ComponentModel;
global using CommunityToolkit.Mvvm.Input;

global using Avalonia;
global using Avalonia.Controls;
global using Avalonia.Threading;
global using Avalonia.Controls.Templates;

global using CreatFlow.NewApp.Services;
global using CreatFlow.NewApp.Services.AccountLogin;
global using CreatFlow.NewApp.Models;
global using CreatFlow.NewApp.Views;
global using CreatFlow.NewApp.ViewModels;

global using CreatFlow.Shared;
global using CreatFlow.Media;
global using CreatFlow.Media.Tts;
global using CreatFlow.Media.Video;
