using LsmWriteDb.Raft;
using LsmWriteDb.Storage;
using Microsoft.AspNetCore.Mvc;

namespace LsmWriteDb.Transactions;

public static class TransactionEndpoints
{
    public static WebApplication MapTransactionEndpoints(this WebApplication app)
    {
        app.MapPost("/transactions", (TransactionManager transactions, RaftRoleGuard raft) =>
        {
            if (!raft.CanAcceptWrites)
            {
                return raft.WriteRejectedResult();
            }

            var transaction = transactions.Begin();
            return Results.Created($"/transactions/{transaction.TransactionId}", transaction);
        });

        app.MapGet("/transactions/{transactionId:guid}/kv/range", async (
            Guid transactionId,
            [FromQuery] string? start,
            [FromQuery] string? end,
            [FromQuery] int? limit,
            TransactionManager transactions) =>
        {
            if (start is not null && end is not null && string.CompareOrdinal(start, end) > 0)
            {
                return Results.BadRequest(new { error = "start must be less than or equal to end" });
            }

            var result = await transactions.RangeAsync(transactionId, start, end, limit ?? 100);
            return result.FoundTransaction
                ? Results.Ok(result.Rows)
                : Results.NotFound(new { error = "transaction not found" });
        });

        app.MapGet("/transactions/{transactionId:guid}/tables/{table}/kv/range", async (
            Guid transactionId,
            string table,
            [FromQuery] string? start,
            [FromQuery] string? end,
            [FromQuery] int? limit,
            TransactionManager transactions) =>
        {
            if (start is not null && end is not null && string.CompareOrdinal(start, end) > 0)
            {
                return Results.BadRequest(new { error = "start must be less than or equal to end" });
            }

            try
            {
                var result = await transactions.RangeAsync(transactionId, table, start, end, limit ?? 100);
                return result.FoundTransaction
                    ? Results.Ok(result.Rows)
                    : Results.NotFound(new { error = "transaction not found" });
            }
            catch (TableNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapGet("/transactions/{transactionId:guid}/kv/{key}", async (
            Guid transactionId,
            string key,
            TransactionManager transactions) =>
        {
            var result = await transactions.GetAsync(transactionId, key);
            if (!result.FoundTransaction)
            {
                return Results.NotFound(new { error = "transaction not found" });
            }

            return result.Row is null ? Results.NotFound() : Results.Ok(result.Row);
        });

        app.MapGet("/transactions/{transactionId:guid}/tables/{table}/kv/{key}", async (
            Guid transactionId,
            string table,
            string key,
            TransactionManager transactions) =>
        {
            try
            {
                var result = await transactions.GetAsync(transactionId, table, key);
                if (!result.FoundTransaction)
                {
                    return Results.NotFound(new { error = "transaction not found" });
                }

                return result.Row is null ? Results.NotFound() : Results.Ok(result.Row);
            }
            catch (TableNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPut("/transactions/{transactionId:guid}/kv/{key}", (
            Guid transactionId,
            string key,
            [FromBody] TransactionPutValueRequest request,
            TransactionManager transactions,
            RaftRoleGuard raft) =>
        {
            if (!raft.CanAcceptWrites)
            {
                return raft.WriteRejectedResult();
            }

            if (string.IsNullOrWhiteSpace(key))
            {
                return Results.BadRequest(new { error = "key is required" });
            }

            if (request.Value is null)
            {
                return Results.BadRequest(new { error = "value is required" });
            }

            return transactions.TryStagePut(transactionId, key, request.Value, out var transaction)
                ? Results.Ok(transaction)
                : Results.NotFound(new { error = "transaction not found" });
        });

        app.MapPut("/transactions/{transactionId:guid}/tables/{table}/kv/{key}", (
            Guid transactionId,
            string table,
            string key,
            [FromBody] TransactionPutValueRequest request,
            TransactionManager transactions,
            RaftRoleGuard raft) =>
        {
            if (!raft.CanAcceptWrites)
            {
                return raft.WriteRejectedResult();
            }

            if (string.IsNullOrWhiteSpace(key))
            {
                return Results.BadRequest(new { error = "key is required" });
            }

            if (request.Value is null)
            {
                return Results.BadRequest(new { error = "value is required" });
            }

            try
            {
                return transactions.TryStagePut(transactionId, table, key, request.Value, out var transaction)
                    ? Results.Ok(transaction)
                    : Results.NotFound(new { error = "transaction not found" });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapDelete("/transactions/{transactionId:guid}/kv/{key}", (
            Guid transactionId,
            string key,
            TransactionManager transactions,
            RaftRoleGuard raft) =>
        {
            if (!raft.CanAcceptWrites)
            {
                return raft.WriteRejectedResult();
            }

            if (string.IsNullOrWhiteSpace(key))
            {
                return Results.BadRequest(new { error = "key is required" });
            }

            return transactions.TryStageDelete(transactionId, key, out var transaction)
                ? Results.Ok(transaction)
                : Results.NotFound(new { error = "transaction not found" });
        });

        app.MapDelete("/transactions/{transactionId:guid}/tables/{table}/kv/{key}", (
            Guid transactionId,
            string table,
            string key,
            TransactionManager transactions,
            RaftRoleGuard raft) =>
        {
            if (!raft.CanAcceptWrites)
            {
                return raft.WriteRejectedResult();
            }

            if (string.IsNullOrWhiteSpace(key))
            {
                return Results.BadRequest(new { error = "key is required" });
            }

            try
            {
                return transactions.TryStageDelete(transactionId, table, key, out var transaction)
                    ? Results.Ok(transaction)
                    : Results.NotFound(new { error = "transaction not found" });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/transactions/{transactionId:guid}/commit", async (
            Guid transactionId,
            TransactionManager transactions,
            RaftRoleGuard raft) =>
        {
            if (!raft.CanAcceptWrites)
            {
                return raft.WriteRejectedResult();
            }

            try
            {
                var commit = await transactions.CommitAsync(transactionId);
                return commit is null
                    ? Results.NotFound(new { error = "transaction not found" })
                    : Results.Ok(commit);
            }
            catch (TableNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapDelete("/transactions/{transactionId:guid}", (
            Guid transactionId,
            TransactionManager transactions,
            RaftRoleGuard raft) =>
        {
            if (!raft.CanAcceptWrites)
            {
                return raft.WriteRejectedResult();
            }

            return transactions.Rollback(transactionId)
                ? Results.NoContent()
                : Results.NotFound(new { error = "transaction not found" });
        });

        return app;
    }
}

public sealed record TransactionPutValueRequest(string? Value);
