using Microsoft.UI.Xaml;
using NovaLink.Lumen.Controls;
using NovaLink.Lumen.Core.Models;
using NovaLink.Lumen.Services;
using NovaLink.Lumen.Services.Minecraft;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NovaLink.Lumen.Views.Dialogs;

/// <summary>Starts a game session and feeds the shared activity stream, mirroring the Electron progress flow.</summary>
public static class LaunchDialog
{
    public static async Task ShowAsync(XamlRoot root, InstanceSummary instance)
    {
        if (LumenRuntime.Launch.State.Phase is GamePhase.Preparing or GamePhase.Starting or GamePhase.Running)
        {
            if (string.Equals(LumenRuntime.Launch.State.InstanceId, instance.Id, StringComparison.OrdinalIgnoreCase)
                && LumenRuntime.Launch.State.Phase == GamePhase.Running)
                LumenRuntime.Launch.Kill();
            else
                await MessageDialog.ShowAsync(root, "游戏正在运行", "请先关闭当前游戏，再启动另一个实例。");
            return;
        }

        var account = LumenRuntime.Accounts.Selected;
        if (account is null)
        {
            var name = await OfflineAccountDialog.ShowAsync(root);
            if (string.IsNullOrWhiteSpace(name)) return;
            try
            {
                await LumenRuntime.Accounts.CreateOfflineAsync(name);
                account = LumenRuntime.Accounts.Selected;
            }
            catch (Exception ex)
            {
                await MessageDialog.ShowAsync(root, "创建账户失败", ex.Message);
                return;
            }
        }
        if (account is null) return;

        var taskId = "launch:" + instance.Id;
        using var cts = new CancellationTokenSource();
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ProgressCenter.Start(taskId, "启动 " + instance.Name, instance.Loader + " · " + instance.Kernel,
            "launch", "准备启动", "正在检查 Java 与启动参数", true, () => cts.Cancel());

        void Stage(string phase, double percent, string message)
            => ProgressCenter.Update(taskId, phase, percent, message);
        void Output(string line) => ProgressCenter.Log(taskId, line);
        void State(GameState state)
        {
            if (!string.Equals(state.InstanceId, instance.Id, StringComparison.OrdinalIgnoreCase)) return;
            switch (state.Phase)
            {
                case GamePhase.Preparing:
                    ProgressCenter.Update(taskId, "准备启动", null, state.Message);
                    break;
                case GamePhase.Starting:
                    ProgressCenter.Update(taskId, "启动进程", 76, state.Message);
                    break;
                case GamePhase.Running:
                    // Keep the task active for the whole session so the dock, status chip and
                    // stop button remain reachable after the Java process starts.
                    ProgressCenter.Update(taskId, "运行中", 100,
                        "游戏运行中 · PID " + state.Pid + " · 可随时停止");
                    ProgressCenter.BindStop(taskId, LumenRuntime.Launch.Kill);
                    break;
                case GamePhase.Exited:
                    ProgressCenter.Finish(taskId, "游戏进程已退出");
                    exited.TrySetResult(true);
                    break;
                case GamePhase.Crashed:
                    ProgressCenter.Finish(taskId, state.Message, failed: true);
                    exited.TrySetResult(true);
                    break;
            }
        }

        LumenRuntime.Launch.Stage += Stage;
        LumenRuntime.Launch.Output += Output;
        LumenRuntime.Launch.StateChanged += State;
        try
        {
            AppLog.Info("launch", "打开进度显示 " + taskId);
            ProgressCenter.OpenOverlay();
            AppLog.Info("launch", "进度显示已打开 " + taskId);
        }
        catch (Exception ex)
        {
            AppLog.Error("launch", "进度显示打开失败", ex);
        }
        try
        {
            await LumenRuntime.Launch.LaunchAsync(instance, account, cts.Token);
            // LaunchAsync returns as soon as the process is alive. Keep the activity task
            // alive until the game exits so "运行中" and 停止游戏 do not disappear.
            if (LumenRuntime.Launch.State.Phase == GamePhase.Running)
                await exited.Task;
        }
        catch (OperationCanceledException)
        {
            ProgressCenter.Finish(taskId, "已取消启动", failed: true, cancelled: true);
        }
        catch (Exception ex)
        {
            AppLog.Error("launch", "启动失败", ex);
            ProgressCenter.Log(taskId, ex.Message);
            ProgressCenter.Finish(taskId, ex.Message, failed: true);
        }
        finally
        {
            LumenRuntime.Launch.Stage -= Stage;
            LumenRuntime.Launch.Output -= Output;
            LumenRuntime.Launch.StateChanged -= State;
        }
    }
}
