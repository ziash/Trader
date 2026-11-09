using System.Text.Json;
using Trader.TM;

namespace Trader.TM.Tests;

/// <summary>Scout reads which main each mirror belongs to from TM's saved state (scout.md §2.9, Trader.Scout TmLinks): these names are that contract.</summary>
public sealed class StateFileTests
{
    [Fact]
    public void The_saved_state_gives_each_trades_main_and_mirror_positions()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tm-state-{Guid.NewGuid():N}.json");
        try
        {
            var book = new TradeBook(path);
            book.Trades.Add(new TradeState { MainId = 10, Mirror = new LegState { PositionId = 20 } });
            book.Save();
            using var saved = JsonDocument.Parse(File.ReadAllText(path));
            var trade = saved.RootElement[0];
            Assert.Equal(10, trade.GetProperty("MainId").GetInt64());
            Assert.Equal(20, trade.GetProperty("Mirror").GetProperty("PositionId").GetInt64());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
