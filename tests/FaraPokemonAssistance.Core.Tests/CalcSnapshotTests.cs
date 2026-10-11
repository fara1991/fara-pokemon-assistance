using System.Text;
using FaraPokemonAssistance.Core.Battle;
using FaraPokemonAssistance.Core.Data;
using FaraPokemonAssistance.Core.Models;
using Xunit;

namespace FaraPokemonAssistance.Core.Tests;

/// <summary>
/// 今のゲーム（剣盾・SV・チャンピオンズ）のダメージ計算・素早さが変わっていないことを確かめる。
/// いろいろな条件の組み合わせ（固定の乱数で作る）の結果を Data/calc_snapshot.txt と比べる。
/// 計算を意図して変えたときだけ、環境変数 CALC_SNAPSHOT_WRITE=1 でテストを実行して作り直す。
/// </summary>
public class CalcSnapshotTests
{
    private const int CasesPerDataSet = 600;

    /// <summary>実行環境によらず同じ並びになる乱数（線形合同法）。</summary>
    private sealed class Lcg(ulong seed)
    {
        private ulong _state = seed;
        public int Next(int max)
        {
            _state = _state * 6364136223846793005UL + 1442695040888963407UL;
            return (int)((_state >> 33) % (ulong)max);
        }
        public T Pick<T>(IReadOnlyList<T> list) => list[Next(list.Count)];
        public bool Chance(int percent) => Next(100) < percent;
    }

    private static string SnapshotPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FaraPokemonAssistance.sln")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "tests", "FaraPokemonAssistance.Core.Tests", "Data", "calc_snapshot.txt");
    }

    internal static async Task<string> BuildSnapshotAsync()
    {
        var sb = new StringBuilder();
        var catalog = TestData.Catalog();
        foreach (var key in new[] { "Gen8", "Gen9", "Champions" })
        {
            var data = await catalog.GetDataSetAsync(key);
            var rng = new Lcg(key switch { "Gen8" => 8UL, "Gen9" => 9UL, _ => 99UL });
            var pokemon = data.Pokemon.Where(p => !p.IsProvisional).ToList();
            var natures = data.Natures;
            var items = data.Items;
            var calc = new DamageCalculator(data.TypeChart);
            var evPresets = new[]
            {
                new StatSet(), new StatSet { Attack = 252, Speed = 252, HP = 4 }, new StatSet { SpAttack = 252, Speed = 252, HP = 4 },
                new StatSet { HP = 252, Defense = 252, SpDefense = 4 }, new StatSet { HP = 252, SpDefense = 252, Defense = 4 },
            };
            PokemonBuild Build(Pokemon p)
            {
                var b = new PokemonBuild(p, data.Info.EvSystem)
                {
                    Level = rng.Chance(80) ? 50 : 100,
                    Nature = rng.Pick(natures),
                    Item = rng.Chance(40) ? null : rng.Pick(items),
                };
                var preset = rng.Pick(evPresets);
                b.EVs = data.Info.EvSystem == EvSystem.Points
                    ? new StatSet(Math.Min(32, preset.HP), Math.Min(32, preset.Attack), Math.Min(32, preset.Defense), Math.Min(32, preset.SpAttack), Math.Min(32, preset.SpDefense), Math.Min(2, preset.Speed))
                    : preset.Clone();
                var abilities = data.AbilitiesOf(p);
                b.Ability = abilities.Count == 0 ? null : rng.Pick(abilities);
                if (rng.Chance(30))
                {
                    b.Boosts[Stat.Attack] = rng.Next(5) - 2;
                    b.Boosts[Stat.SpAttack] = rng.Next(5) - 2;
                    b.Boosts[Stat.Defense] = rng.Next(5) - 2;
                    b.Boosts[Stat.SpDefense] = rng.Next(5) - 2;
                    b.Boosts[Stat.Speed] = rng.Next(5) - 2;
                }
                var status = rng.Next(20);
                b.Status = status switch { 0 => StatusCondition.Burn, 1 => StatusCondition.Paralysis, 2 => StatusCondition.Poison, 3 => StatusCondition.BadlyPoisoned, _ => StatusCondition.None };
                if (data.Info.HasTerastal && rng.Chance(20)) b.TeraType = rng.Pick(data.TypeChart.Types);
                if (data.Info.HasDynamax && rng.Chance(15)) b.IsDynamax = true;
                if (rng.Chance(20)) b.HpPercent = 1 + rng.Next(100);
                else if (rng.Chance(10)) b.CurrentHp = 1 + rng.Next(Math.Max(1, b.MaxHp));
                return b;
            }

            for (var i = 0; i < CasesPerDataSet; i++)
            {
                var attacker = Build(rng.Pick(pokemon));
                var defender = Build(rng.Pick(pokemon));
                var moves = data.LearnableMoves(attacker.Pokemon.Id).Where(m => m.IsDamaging).ToList();
                if (moves.Count == 0) continue;
                var move = rng.Pick(moves);
                var request = new DamageRequest
                {
                    Attacker = attacker,
                    Defender = defender,
                    Move = move,
                    Format = rng.Chance(25) ? BattleFormat.Doubles : BattleFormat.Singles,
                    IsCritical = rng.Chance(15),
                    Weather = (Weather)rng.Next(5),
                    Terrain = rng.Chance(30) ? (Terrain)rng.Next(5) : Terrain.None,
                    Reflect = rng.Chance(10),
                    LightScreen = rng.Chance(10),
                    Screen = rng.Chance(5),
                };
                var r = calc.Calculate(request);
                var tailwind = rng.Chance(20);
                var speed = SpeedCalculator.Effective(attacker, request.Weather, request.Terrain, tailwind).Speed;
                sb.Append(key).Append('|').Append(i).Append('|')
                    .Append(attacker.Pokemon.Id).Append('>').Append(move.Id).Append('>').Append(defender.Pokemon.Id).Append('|')
                    .Append(string.Join(",", r.Rolls)).Append('|').Append(r.KnockOut.Hits).Append(':').Append(r.KnockOut.Probability.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture))
                    .Append('|').Append(r.TypeEffectiveness.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append("|S").Append(speed).Append('\n');
            }
        }
        return sb.ToString();
    }

    [Fact]
    public async Task Current_games_results_are_unchanged()
    {
        var actual = await BuildSnapshotAsync();
        var path = SnapshotPath();
        if (Environment.GetEnvironmentVariable("CALC_SNAPSHOT_WRITE") == "1")
        {
            await File.WriteAllTextAsync(path, actual);
            return;
        }
        var expected = (await File.ReadAllTextAsync(path)).Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var lines = actual.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(expected.Length, lines.Length);
        var diffs = expected.Zip(lines).Where(x => x.First != x.Second).Take(5).Select(x => $"expected {x.First}\nactual   {x.Second}").ToList();
        Assert.True(diffs.Count == 0, string.Join("\n", diffs));
    }
}
