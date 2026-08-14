---
document type: cmdlet
external help file: Devolutions.Ahtola.PowerShell.dll-Help.xml
HelpUri: ''
Locale: en-US
Module Name: Devolutions.Ahtola.Sqlite
ms.date: 08/14/2026
PlatyPS schema version: 2024-05-01
title: Start-AhtolaSqliteTransaction
---

# Start-AhtolaSqliteTransaction

## SYNOPSIS

Starts an ADO.NET transaction on an Ahtola connection.

## SYNTAX

### __AllParameterSets

```
Start-AhtolaSqliteTransaction -Connection <DbConnection> [-IsolationLevel <IsolationLevel>]
 [-Deferred]
```

## ALIASES

This cmdlet has no aliases.

## DESCRIPTION

Opens the caller-owned connection if needed and begins a transaction with the selected isolation level. `Deferred` works only with local Ahtola SQLite connections.

## EXAMPLES

### Example 1

This example uses a caller-owned connection and closes it when complete.

```powershell
$connection = New-AhtolaSqliteConnection
try {
    Start-AhtolaSqliteTransaction -Connection $connection
} finally { $connection | Close-AhtolaSqliteConnection -Confirm:$false }
```

## PARAMETERS

### -Connection

Caller-owned Ahtola connection. It can be opened if needed but is never closed or disposed by this cmdlet.

```yaml
Type: System.Data.Common.DbConnection
DefaultValue: ''
SupportsWildcards: false
Aliases:
- SqliteConnection
ParameterSets:
- Name: (All)
  Position: Named
  IsRequired: true
  ValueFromPipeline: true
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -Deferred

Starts a local transaction with `BEGIN DEFERRED`; unsupported by Turso Cloud connections.

```yaml
Type: System.Management.Automation.SwitchParameter
DefaultValue: ''
SupportsWildcards: false
Aliases: []
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

### -IsolationLevel

ADO.NET isolation level for the transaction. The default is `Serializable`.

```yaml
Type: System.Data.IsolationLevel
DefaultValue: ''
SupportsWildcards: false
Aliases: []
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

### System.Data.Common.DbConnection

A value can be piped to the pipeline-enabled parameter.

## OUTPUTS

### System.Data.Common.DbTransaction

An active caller-owned `DbTransaction`.

## NOTES

Complete or undo the returned transaction before disposing its connection.

## RELATED LINKS

[PowerShell module guide](https://github.com/Devolutions/ahtola/blob/master/docs/powershell-module.md)
