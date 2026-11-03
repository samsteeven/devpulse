using DevPulse.Agent;

namespace DevPulse.Agent.Tests;

public class SystemMetricsTests
{
    private const string Stat1 = "cpu  100 0 50 800 50 0 0 0 0 0\ncpu0 50 0 25 400 25 0 0 0 0 0\nintr 1";
    private const string Stat2 = "cpu  160 0 80 860 60 0 0 0 0 0\ncpu0 80 0 40 430 30 0 0 0 0 0\nintr 2";

    [Fact]
    public void La_charge_processeur_se_calcule_entre_deux_lectures()
    {
        var first = ProcParser.ParseStat(Stat1);
        var second = ProcParser.ParseStat(Stat2);

        Assert.Equal(850UL, first.Idle); // idle + iowait
        Assert.Equal(1000UL, first.Total);
        // Total +160, inactif +70 : 90 / 160.
        Assert.Equal(56.25, CpuTimes.UsagePercent(first, second), 2);
    }

    [Fact]
    public void Un_compteur_qui_recule_donne_zero_plutot_qu_une_valeur_absurde() =>
        Assert.Equal(0, CpuTimes.UsagePercent(ProcParser.ParseStat(Stat2), ProcParser.ParseStat(Stat1)));

    [Fact]
    public void La_memoire_utilisee_compte_le_cache_comme_disponible()
    {
        const string meminfo = "MemTotal:       16000000 kB\nMemFree:         1000000 kB\nMemAvailable:    4000000 kB\nBuffers: 1 kB\n";
        Assert.Equal(75, ProcParser.ParseMemoryPercent(meminfo), 3);
    }

    [Fact]
    public void La_duree_de_fonctionnement_est_lue_en_secondes() =>
        Assert.Equal(354207, ProcParser.ParseUptime("354207.81 1384627.03\n"));

    [Fact]
    public void Les_mesures_du_systeme_courant_sont_dans_les_bornes()
    {
        ISystemMetrics metrics = OperatingSystem.IsWindows() ? new WindowsMetrics() : new LinuxMetrics();
        metrics.Read();
        Thread.Sleep(200);
        var snapshot = metrics.Read();

        Assert.InRange(snapshot.CpuPercent, 0, 100);
        Assert.InRange(snapshot.MemoryPercent, 0.1, 100);
        Assert.InRange(snapshot.DiskPercent, 0.1, 100);
        Assert.True(snapshot.UptimeSeconds > 0);
    }
}
