---
document type: module
Help Version: 1.0.0.0
HelpInfoUri: 
Locale: en-US
Module Guid: b7c2f0d1-8a4e-4f6b-9c3d-2e1a0b9f8d7c
Module Name: Devolutions.Ahtola.Sqlite
ms.date: 08/14/2026
PlatyPS schema version: 2024-05-01
title: Devolutions.Ahtola.Sqlite Module
---

# Devolutions.Ahtola.Sqlite Module

## Description

`Devolutions.Ahtola.Sqlite` exposes the pure-managed Ahtola SQLite engine to PowerShell 7.4 or later. It provides local connections, parameterized queries, configuration-driven row operations, transactions, schema and maintenance commands, table import/export, managed passwords, and optional Turso Cloud or replica connections.

Import it with `Import-Module Devolutions.Ahtola.Sqlite`. It requires PowerShell 7.4+ on .NET 8+; Windows PowerShell 5.1 is unsupported. Local operations do not require a native SQLite library. Connections returned by `New-AhtolaSqliteConnection` are caller-owned and should be closed with `Close-AhtolaSqliteConnection`. Commands that change data, files, pools, passwords, or replicas support `-WhatIf` and `-Confirm`.

## Devolutions.Ahtola.Sqlite

### [Backup-AhtolaSqliteDatabase](Backup-AhtolaSqliteDatabase.md)

Copies a local Ahtola SQLite database to another connection.

### [Checkpoint-AhtolaSqliteDatabase](Checkpoint-AhtolaSqliteDatabase.md)

Runs a WAL checkpoint on a local Ahtola database.

### [Clear-AhtolaSqliteConnectionPool](Clear-AhtolaSqliteConnectionPool.md)

Clears the pool for a local Ahtola connection.

### [Clear-AhtolaSqlitePassword](Clear-AhtolaSqlitePassword.md)

Clears the Ahtola-managed password from a local database.

### [Close-AhtolaSqliteConnection](Close-AhtolaSqliteConnection.md)

Closes and disposes an Ahtola connection, or clears all local pools.

### [Compare-AhtolaSqliteDatabaseVersion](Compare-AhtolaSqliteDatabaseVersion.md)

Compares a configured database metadata version with an expected version.

### [Complete-AhtolaSqliteTransaction](Complete-AhtolaSqliteTransaction.md)

Commits a transaction or releases a savepoint.

### [Export-AhtolaSqliteTable](Export-AhtolaSqliteTable.md)

Exports a table or query result to JSON or CSV.

### [Get-AhtolaSqliteDatabaseInfo](Get-AhtolaSqliteDatabaseInfo.md)

Retrieves basic page, journal, and connection state information.

### [Get-AhtolaSqliteDatabaseMetadata](Get-AhtolaSqliteDatabaseMetadata.md)

Retrieves stored Ahtola SQLite metadata values.

### [Get-AhtolaSqliteIndex](Get-AhtolaSqliteIndex.md)

Lists indexes and their definitions.

### [Get-AhtolaSqliteReplicaChangeCapture](Get-AhtolaSqliteReplicaChangeCapture.md)

Projects pending managed-replica changes into the public CDC contract.

### [Get-AhtolaSqliteReplicaConflict](Get-AhtolaSqliteReplicaConflict.md)

Inspects a managed replica's durable push conflict.

### [Get-AhtolaSqliteRow](Get-AhtolaSqliteRow.md)

Retrieves rows from a configured table or view.

### [Get-AhtolaSqliteSchema](Get-AhtolaSqliteSchema.md)

Retrieves an ADO.NET schema collection from a local Ahtola connection.

### [Get-AhtolaSqliteTable](Get-AhtolaSqliteTable.md)

Lists user tables and their CREATE statements.

### [Import-AhtolaSqliteTable](Import-AhtolaSqliteTable.md)

Imports JSON or CSV rows into a local SQLite table.

### [Invoke-AhtolaSqliteBulkCopy](Invoke-AhtolaSqliteBulkCopy.md)

Bulk inserts piped objects into a local SQLite table.

### [Invoke-AhtolaSqliteMaintenance](Invoke-AhtolaSqliteMaintenance.md)

Runs an explicit SQLite maintenance operation.

### [Invoke-AhtolaSqliteQuery](Invoke-AhtolaSqliteQuery.md)

Executes parameterized SQL against an Ahtola local or Turso Cloud connection.

### [Invoke-AhtolaSqliteReplicaSync](Invoke-AhtolaSqliteReplicaSync.md)

Synchronizes a managed Turso Cloud replica with its remote endpoint.

### [New-AhtolaSqliteConnection](New-AhtolaSqliteConnection.md)

Creates and opens a local Ahtola SQLite, Turso Cloud, or managed replica connection.

### [New-AhtolaSqliteRow](New-AhtolaSqliteRow.md)

Inserts a row into a configured SQLite table.

### [Optimize-AhtolaSqliteDatabase](Optimize-AhtolaSqliteDatabase.md)

Runs VACUUM, optionally followed by ANALYZE, on a local database.

### [Remove-AhtolaSqliteRow](Remove-AhtolaSqliteRow.md)

Deletes rows from a configured SQLite table.

### [Resolve-AhtolaSqliteReplicaConflict](Resolve-AhtolaSqliteReplicaConflict.md)

Rebases eligible changes or discards unresolved managed-replica changes.

### [Save-AhtolaSqliteTransaction](Save-AhtolaSqliteTransaction.md)

Creates a named savepoint in an active transaction.

### [Set-AhtolaSqlitePassword](Set-AhtolaSqlitePassword.md)

Sets or rotates the Ahtola-managed password for a local database.

### [Set-AhtolaSqliteRow](Set-AhtolaSqliteRow.md)

Updates or upserts rows in a configured SQLite table.

### [Start-AhtolaSqliteTransaction](Start-AhtolaSqliteTransaction.md)

Starts an ADO.NET transaction on an Ahtola connection.

### [Test-AhtolaSqliteConnection](Test-AhtolaSqliteConnection.md)

Tests an Ahtola local or Turso Cloud connection with `SELECT 1`.

### [Test-AhtolaSqliteIntegrity](Test-AhtolaSqliteIntegrity.md)

Runs SQLite `PRAGMA integrity_check`.

### [Undo-AhtolaSqliteTransaction](Undo-AhtolaSqliteTransaction.md)

Rolls back a transaction or rolls back to a savepoint.
