namespace DragonHead.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // 키를 바꾸고 다시 시작할 때는 이전 프로세스가 끝날 때까지 잠깐 기다린다
        using var mutex = new Mutex(false, @"Local\DragonHead.SingleInstance");
        bool owned;
        try { owned = mutex.WaitOne(args.Contains("--restart") ? 8000 : 0); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned)
        {
            MessageBox.Show("용머리 알리미가 이미 실행 중입니다. 작업 표시줄 오른쪽 트레이를 확인하세요.",
                "용머리 알리미", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Write("UI 예외: " + e.Exception);

        AppSettingsHolder holder;
        try
        {
            holder = SettingsStore.Load(out bool created);
            if (created || string.IsNullOrWhiteSpace(holder.Settings.ClientId) || string.IsNullOrWhiteSpace(holder.Secret))
            {
                // 키 입력 창: 저장하면 설정 파일에 들어가고 바로 시작
                using var setup = new KeySetupForm(holder.Settings, holder.Secret, firstRun: true);
                if (setup.ShowDialog() != DialogResult.OK) return;
                holder = new AppSettingsHolder(holder.Settings, setup.Secret);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("설정 파일을 읽지 못했습니다.\n\n" + ex.Message + "\n\n" + SettingsStore.SettingsPath,
                "용머리 알리미", MessageBoxButtons.OK, MessageBoxIcon.Error);
            SettingsStore.OpenInEditor();
            return;
        }

        try
        {
            Application.Run(new TrayContext(holder));
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }
}
