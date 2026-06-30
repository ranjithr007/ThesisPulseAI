using System.Data;
using Microsoft.Data.SqlClient;

namespace ThesisPulse.Shared.Infrastructure.Portfolio;

public sealed partial class SqlServerPortfolioLedgerStore
{
    private async Task ApplyCashAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        FillRow fill,
        CancellationToken cancellationToken)
    {
        var gross = fill.Quantity * fill.Price;
        var cashDelta = fill.Side == "BUY"
            ? -(gross + fill.FeesAmount + fill.TaxesAmount)
            : gross - fill.FeesAmount - fill.TaxesAmount;
        if (cashDelta == 0m)
        {
            return;
        }

        const string readSql = """
            SELECT TOP (1)
                [cash_balance_id], [settled_amount], [unsettled_receivable_amount],
                [unsettled_payable_amount], [reserved_amount],
                [current_balance_version], [last_ledger_sequence]
            FROM [portfolio].[cash_balances] WITH (UPDLOCK, HOLDLOCK)
            WHERE [portfolio_id] = @portfolio_id
              AND [currency_code] = @currency;
            """;
        await using var read = Command(connection, transaction, readSql);
        read.Parameters.Add("@portfolio_id", SqlDbType.BigInt).Value = fill.PortfolioId;
        read.Parameters.Add("@currency", SqlDbType.Char, 3).Value = fill.CurrencyCode;
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);

        long? balanceId = null;
        decimal settled = 0m;
        decimal receivable = 0m;
        decimal payable = 0m;
        decimal reserved = 0m;
        var version = 0;
        long sequence = 0;
        if (await reader.ReadAsync(cancellationToken))
        {
            balanceId = reader.GetInt64(0);
            settled = reader.GetDecimal(1);
            receivable = reader.GetDecimal(2);
            payable = reader.GetDecimal(3);
            reserved = reader.GetDecimal(4);
            version = reader.GetInt32(5);
            sequence = reader.GetInt64(6);
        }

        await reader.DisposeAsync();
        settled += cashDelta;
        version++;
        sequence++;
        var total = settled + receivable - payable;
        var available = total - reserved;

        if (balanceId is null)
        {
            const string insertSql = """
                INSERT INTO [portfolio].[cash_balances]
                (
                    [portfolio_id], [currency_code], [settled_amount],
                    [unsettled_receivable_amount], [unsettled_payable_amount],
                    [reserved_amount], [total_balance_amount], [available_amount],
                    [current_balance_version], [last_ledger_sequence], [as_of_utc],
                    [created_by], [updated_by]
                )
                OUTPUT INSERTED.[cash_balance_id]
                VALUES
                (
                    @portfolio_id, @currency, @settled,
                    0, 0,
                    0, @total, @available,
                    @version, @sequence, @as_of,
                    @actor, @actor
                );
                """;
            await using var insert = Command(connection, transaction, insertSql);
            AddCashParameters(
                insert,
                fill,
                settled,
                total,
                available,
                version,
                sequence);
            balanceId = Convert.ToInt64(
                await insert.ExecuteScalarAsync(cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture);
        }
        else
        {
            const string updateSql = """
                UPDATE [portfolio].[cash_balances]
                SET [settled_amount] = @settled,
                    [total_balance_amount] = @total,
                    [available_amount] = @available,
                    [current_balance_version] = @version,
                    [last_ledger_sequence] = @sequence,
                    [as_of_utc] = @as_of,
                    [updated_at_utc] = @as_of,
                    [updated_by] = @actor
                WHERE [cash_balance_id] = @balance_id
                  AND [current_balance_version] = @expected_version;
                """;
            await using var update = Command(connection, transaction, updateSql);
            AddCashParameters(
                update,
                fill,
                settled,
                total,
                available,
                version,
                sequence);
            update.Parameters.Add("@balance_id", SqlDbType.BigInt).Value = balanceId.Value;
            update.Parameters.Add("@expected_version", SqlDbType.Int).Value = version - 1;
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException("Cash projection changed concurrently.");
            }
        }

        const string ledgerSql = """
            INSERT INTO [portfolio].[cash_ledger_entries]
            (
                [cash_ledger_entry_uid], [portfolio_id], [cash_balance_id],
                [fill_id], [order_id], [ledger_sequence], [idempotency_key],
                [entry_type], [currency_code], [settled_delta_amount],
                [unsettled_receivable_delta_amount], [unsettled_payable_delta_amount],
                [reserved_delta_amount], [effective_at_utc], [posted_at_utc],
                [description], [correlation_id], [created_by]
            )
            VALUES
            (
                NEWID(), @portfolio_id, @balance_id,
                @fill_id, @order_id, @sequence, @idempotency_key,
                'TRADE_SETTLEMENT', @currency, @delta,
                0, 0,
                0, @effective_at, @effective_at,
                @description, @correlation_id, @actor
            );
            """;
        await using var ledger = Command(connection, transaction, ledgerSql);
        ledger.Parameters.Add("@portfolio_id", SqlDbType.BigInt).Value = fill.PortfolioId;
        ledger.Parameters.Add("@balance_id", SqlDbType.BigInt).Value = balanceId.Value;
        ledger.Parameters.Add("@fill_id", SqlDbType.BigInt).Value = fill.FillId;
        ledger.Parameters.Add("@order_id", SqlDbType.BigInt).Value = fill.OrderId;
        ledger.Parameters.Add("@sequence", SqlDbType.BigInt).Value = sequence;
        ledger.Parameters.Add("@idempotency_key", SqlDbType.VarChar, 200).Value =
            $"FILL:{fill.FillUid:N}";
        ledger.Parameters.Add("@currency", SqlDbType.Char, 3).Value = fill.CurrencyCode;
        AddDecimal(ledger, "@delta", cashDelta);
        AddDateTime(ledger, "@effective_at", fill.FillAtUtc);
        ledger.Parameters.Add("@description", SqlDbType.NVarChar, 1000).Value =
            $"PAPER {fill.Side} fill {fill.FillUid:D}";
        ledger.Parameters.Add("@correlation_id", SqlDbType.UniqueIdentifier).Value =
            fill.CorrelationId;
        ledger.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = _options.Actor;
        await ledger.ExecuteNonQueryAsync(cancellationToken);
    }

    private void AddCashParameters(
        SqlCommand command,
        FillRow fill,
        decimal settled,
        decimal total,
        decimal available,
        int version,
        long sequence)
    {
        command.Parameters.Add("@portfolio_id", SqlDbType.BigInt).Value = fill.PortfolioId;
        command.Parameters.Add("@currency", SqlDbType.Char, 3).Value = fill.CurrencyCode;
        AddDecimal(command, "@settled", settled);
        AddDecimal(command, "@total", total);
        AddDecimal(command, "@available", available);
        command.Parameters.Add("@version", SqlDbType.Int).Value = version;
        command.Parameters.Add("@sequence", SqlDbType.BigInt).Value = sequence;
        AddDateTime(command, "@as_of", fill.FillAtUtc);
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = _options.Actor;
    }
}
