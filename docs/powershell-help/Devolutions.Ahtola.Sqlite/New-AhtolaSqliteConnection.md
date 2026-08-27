---
document type: cmdlet
external help file: Devolutions.Ahtola.PowerShell.dll-Help.xml
HelpUri: ''
Locale: en-US
Module Name: Devolutions.Ahtola.Sqlite
ms.date: 08/14/2026
PlatyPS schema version: 2024-05-01
title: New-AhtolaSqliteConnection
---

# New-AhtolaSqliteConnection

## SYNOPSIS

Creates and opens a local Ahtola SQLite, Turso Cloud, or managed replica connection.

## SYNTAX

### byConnectionString (Default)

```
New-AhtolaSqliteConnection [-ConnectionString <string>] [-ReadOnly] [-WhatIf] [-Confirm]
```

### byDatabasePath

```
New-AhtolaSqliteConnection -DatabaseFile <string> [-DatabasePath <string>] [-ReadOnly] [-WhatIf]
 [-Confirm]
```

### byTursoCloud

```
New-AhtolaSqliteConnection [-TursoUrl <string>] [-AuthToken <securestring>] [-UseTursoEnvironment]
 [-WhatIf] [-Confirm]
```

### byTursoReplica

```
New-AhtolaSqliteConnection -ReplicaPath <string> [-TursoUrl <string>] [-AuthToken <securestring>]
 [-UseTursoEnvironment] [-SyncInterval <int>] [-LongPollTimeout <timespan>]
 [-PushOperationsThreshold <long>] [-PullBytesThreshold <long>] [-WhatIf] [-Confirm]
```

### byTursoReplicaPrefix

```
New-AhtolaSqliteConnection -ReplicaPath <string> -BootstrapPrefixBytes <int> [-TursoUrl <string>]
 [-AuthToken <securestring>] [-UseTursoEnvironment] [-SyncInterval <int>]
 [-LongPollTimeout <timespan>] [-PushOperationsThreshold <long>] [-PullBytesThreshold <long>]
 [-BootstrapSegmentBytes <long>] [-BootstrapPrefetch] [-WhatIf] [-Confirm]
```

### byTursoReplicaQuery

```
New-AhtolaSqliteConnection -ReplicaPath <string> -BootstrapQuery <string> [-TursoUrl <string>]
 [-AuthToken <securestring>] [-UseTursoEnvironment] [-SyncInterval <int>]
 [-LongPollTimeout <timespan>] [-PushOperationsThreshold <long>]
 [-BootstrapSegmentBytes <long>] [-BootstrapPrefetch] [-WhatIf] [-Confirm]
```

### byTursoReplicaEncrypted

```
New-AhtolaSqliteConnection -ReplicaPath <string> -RemoteEncryptionKey <securestring>
 -RemoteEncryptionCipher <string> [-TursoUrl <string>] [-AuthToken <securestring>]
 [-UseTursoEnvironment] [-SyncInterval <int>] [-LongPollTimeout <timespan>]
 [-PushOperationsThreshold <long>] [-PullBytesThreshold <long>] [-WhatIf] [-Confirm]
```

## ALIASES

This cmdlet has no aliases.

## DESCRIPTION

Creates a connection from a connection string, database path and file, or Turso endpoint. Replica parameter sets expose long polling, push/pull thresholds, prefix/query bootstrap, and remote encryption. The returned connection is open and caller-owned. Local relative paths resolve from the current filesystem location.

## EXAMPLES

### Example 1

This example uses a caller-owned connection and closes it when complete.

```powershell
$connection = New-AhtolaSqliteConnection
try {
    Invoke-AhtolaSqliteQuery -Connection $connection -CommandText 'SELECT 1 AS Value;'
} finally { $connection | Close-AhtolaSqliteConnection -Confirm:$false }
```

## PARAMETERS

### -AuthToken

Secure Turso authentication token used only while opening the cloud connection.

```yaml
Type: System.Security.SecureString
DefaultValue: ''
SupportsWildcards: false
Aliases:
- Token
ParameterSets:
- Name: byTursoCloud
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
- Name: byTursoReplicaPrefix
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
- Name: byTursoReplicaQuery
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
- Name: byTursoReplicaEncrypted
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
- Name: byTursoReplica
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -BootstrapPrefetch

Prefetches adjacent pages when a partially bootstrapped replica faults a missing page.

```yaml
Type: System.Management.Automation.SwitchParameter
DefaultValue: ''
SupportsWildcards: false
Aliases: []
ParameterSets:
- Name: byTursoReplicaPrefix
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
- Name: byTursoReplicaQuery
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -BootstrapPrefixBytes

Bootstraps complete 4 KiB pages covered by this initial byte prefix.

```yaml
Type: System.Int32
DefaultValue: ''
SupportsWildcards: false
Aliases: []
ParameterSets:
- Name: byTursoReplicaPrefix
  Position: Named
  IsRequired: true
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -BootstrapQuery

Server-side SQL query used once to select the initial page set.

```yaml
Type: System.String
DefaultValue: ''
SupportsWildcards: false
Aliases: []
ParameterSets:
- Name: byTursoReplicaQuery
  Position: Named
  IsRequired: true
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -BootstrapSegmentBytes

Lazy-loading segment size. It must be a whole number of 4 KiB pages.

```yaml
Type: System.Int64
DefaultValue: ''
SupportsWildcards: false
Aliases: []
ParameterSets:
- Name: byTursoReplicaPrefix
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
- Name: byTursoReplicaQuery
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -Confirm

Prompts you for confirmation before running the cmdlet.

```yaml
Type: System.Management.Automation.SwitchParameter
DefaultValue: ''
SupportsWildcards: false
Aliases:
- cf
ParameterSets:
- Name: (All)
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -ConnectionString

Ahtola SQLite connection string; relative `Data Source` values resolve from the current filesystem location.

```yaml
Type: System.String
DefaultValue: ''
SupportsWildcards: false
Aliases: []
ParameterSets:
- Name: byConnectionString
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -DatabaseFile

File name to combine with `DatabasePath` for a local connection.

```yaml
Type: System.String
DefaultValue: ''
SupportsWildcards: false
Aliases: []
ParameterSets:
- Name: byDatabasePath
  Position: Named
  IsRequired: true
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -DatabasePath

Directory containing `DatabaseFile`; relative paths resolve from the current filesystem location.

```yaml
Type: System.String
DefaultValue: ''
SupportsWildcards: false
Aliases: []
ParameterSets:
- Name: byDatabasePath
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -LongPollTimeout

Maximum server long-poll duration. Omit this parameter to disable long polling.

```yaml
Type: System.Nullable[System.TimeSpan]
DefaultValue: ''
SupportsWildcards: false
Aliases: []
ParameterSets:
- Name: byTursoReplica
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
- Name: byTursoReplicaPrefix
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
- Name: byTursoReplicaQuery
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
- Name: byTursoReplicaEncrypted
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -PullBytesThreshold

Target byte size for each initial bootstrap pull, rounded to complete 4 KiB pages.

```yaml
Type: System.Int64
DefaultValue: ''
SupportsWildcards: false
Aliases: []
ParameterSets:
- Name: byTursoReplica
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
- Name: byTursoReplicaPrefix
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
- Name: byTursoReplicaEncrypted
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -PushOperationsThreshold

Maximum CDC operation target for one replica push batch.

```yaml
Type: System.Int64
DefaultValue: ''
SupportsWildcards: false
Aliases: []
ParameterSets:
- Name: byTursoReplica
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
- Name: byTursoReplicaPrefix
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
- Name: byTursoReplicaQuery
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
- Name: byTursoReplicaEncrypted
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -ReadOnly

Opens a local database with `Mode=ReadOnly`. It is unavailable for Turso Cloud connections.

```yaml
Type: System.Management.Automation.SwitchParameter
DefaultValue: ''
SupportsWildcards: false
Aliases: []
ParameterSets:
- Name: byConnectionString
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
- Name: byDatabasePath
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -RemoteEncryptionCipher

Cipher configured on the encrypted remote database.

```yaml
Type: System.String
DefaultValue: ''
SupportsWildcards: false
Aliases: []
ParameterSets:
- Name: byTursoReplicaEncrypted
  Position: Named
  IsRequired: true
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues:
- Aes256Gcm
- Aes128Gcm
- Aegis128L
- Aegis128X2
- Aegis128X4
- Aegis256
- Aegis256X2
- Aegis256X4
HelpMessage: ''
```

### -RemoteEncryptionKey

Base64 remote encryption key supplied as a secure string. The key is never written to output.

```yaml
Type: System.Security.SecureString
DefaultValue: ''
SupportsWildcards: false
Aliases: []
ParameterSets:
- Name: byTursoReplicaEncrypted
  Position: Named
  IsRequired: true
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -ReplicaPath

Local database file for a managed Turso replica.

```yaml
Type: System.String
DefaultValue: ''
SupportsWildcards: false
Aliases: []
ParameterSets:
- Name: byTursoReplica
  Position: Named
  IsRequired: true
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
- Name: byTursoReplicaPrefix
  Position: Named
  IsRequired: true
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
- Name: byTursoReplicaQuery
  Position: Named
  IsRequired: true
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
- Name: byTursoReplicaEncrypted
  Position: Named
  IsRequired: true
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -SyncInterval

Replica synchronization interval in seconds. Zero disables interval-based sync.

```yaml
Type: System.Int32
DefaultValue: ''
SupportsWildcards: false
Aliases: []
ParameterSets:
- Name: byTursoReplica
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
- Name: byTursoReplicaPrefix
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
- Name: byTursoReplicaQuery
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
- Name: byTursoReplicaEncrypted
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -TursoUrl

Absolute `libsql`, `https`, or `http` Turso endpoint without user information, query, or fragment.

```yaml
Type: System.String
DefaultValue: ''
SupportsWildcards: false
Aliases:
- RemoteUrl
- Url
ParameterSets:
- Name: byTursoCloud
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
- Name: byTursoReplica
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
- Name: byTursoReplicaPrefix
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
- Name: byTursoReplicaQuery
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
- Name: byTursoReplicaEncrypted
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -UseTursoEnvironment

Uses `TURSO_REMOTE_URL` and `TURSO_AUTH_TOKEN` as defaults if explicit parameters are absent.

```yaml
Type: System.Management.Automation.SwitchParameter
DefaultValue: ''
SupportsWildcards: false
Aliases: []
ParameterSets:
- Name: byTursoCloud
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
- Name: byTursoReplica
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
- Name: byTursoReplicaPrefix
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
- Name: byTursoReplicaQuery
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
- Name: byTursoReplicaEncrypted
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -WhatIf

Runs the command in a mode that only reports what would happen without performing the actions.

```yaml
Type: System.Management.Automation.SwitchParameter
DefaultValue: ''
SupportsWildcards: false
Aliases:
- wi
ParameterSets:
- Name: (All)
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### CommonParameters

This cmdlet supports the common parameters: -Debug, -ErrorAction, -ErrorVariable,
-InformationAction, -InformationVariable, -OutBuffer, -OutVariable, -PipelineVariable,
-ProgressAction, -Verbose, -WarningAction, and -WarningVariable. For more information, see
[about_CommonParameters](https://go.microsoft.com/fwlink/?LinkID=113216).

## INPUTS

None. This cmdlet does not accept pipeline input.

## OUTPUTS

### Ahtola.Data.Sqlite.SqliteConnection

An open local Ahtola SQLite connection.

### Ahtola.PSSqlite.AhtolaCloudConnection

An open direct Turso Cloud connection or managed replica connection.

## NOTES

`-WhatIf` reports the request without opening a connection or creating a local database file. Partial bootstrap cannot be combined with remote encryption, and query bootstrap cannot be combined with `-PullBytesThreshold`. Authentication tokens and encryption keys are never included in output. Close the returned connection explicitly.

## RELATED LINKS

[PowerShell module guide](https://github.com/Devolutions/ahtola/blob/master/docs/powershell-module.md)
