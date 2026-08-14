---
document type: cmdlet
external help file: Devolutions.Ahtola.PowerShell.dll-Help.xml
HelpUri: ''
Locale: en-US
Module Name: Devolutions.Ahtola.Sqlite
ms.date: 08/14/2026
PlatyPS schema version: 2024-05-01
title: Remove-AhtolaSqliteRow
---

# Remove-AhtolaSqliteRow

## SYNOPSIS

Deletes rows from a configured SQLite table.

## SYNTAX

### __AllParameterSets

```
Remove-AhtolaSqliteRow -Configuration <SQLiteDBConfig> -Table <string> [-Where <IDictionary>]
 [-CaseSensitive] [-Connection <SqliteConnection>] [-Transaction <SqliteTransaction>] [-WhatIf]
 [-Confirm]
```

## ALIASES

This cmdlet has no aliases.

## DESCRIPTION

Builds a DELETE from a `SQLiteDBConfig`, table name, and optional equality filter. Without a connection, it uses and disposes a temporary connection from the configuration.

## EXAMPLES

### Example 1

This example uses a caller-owned connection and closes it when complete.

```powershell
$config = [Devolutions.Ahtola.Sqlite.SQLiteDBConfig]::new('Data Source=:memory:')
$connection = New-AhtolaSqliteConnection
try {
    Invoke-AhtolaSqliteQuery -Connection $connection -CommandText "CREATE TABLE Items(Id INTEGER, Name TEXT); INSERT INTO Items VALUES (1, 'One');" -As NonQuery
    Remove-AhtolaSqliteRow -Configuration $config -Connection $connection -Table Items -Where @{ Id = 1}
} finally { $connection | Close-AhtolaSqliteConnection -Confirm:$false }
```

## PARAMETERS

### -CaseSensitive

Makes `Values` and `Where` column-name matching case-sensitive. The default is case-insensitive.

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

### -Configuration

A `SQLiteDBConfig` that supplies the connection string and settings for this operation.

```yaml
Type: Ahtola.PSSqlite.SQLiteDBConfig
DefaultValue: ''
SupportsWildcards: false
Aliases:
- SqliteDBConfig
ParameterSets:
- Name: (All)
  Position: Named
  IsRequired: true
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

### -Connection

Caller-owned Ahtola connection. It can be opened if needed but is never closed or disposed by this cmdlet.

```yaml
Type: Ahtola.Data.Sqlite.SqliteConnection
DefaultValue: ''
SupportsWildcards: false
Aliases:
- SqliteConnection
ParameterSets:
- Name: (All)
  Position: Named
  IsRequired: false
  ValueFromPipeline: true
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -Table

SQLite table name. The cmdlet quotes the identifier; this is not a SQL fragment.

```yaml
Type: System.String
DefaultValue: ''
SupportsWildcards: false
Aliases:
- TableName
ParameterSets:
- Name: (All)
  Position: Named
  IsRequired: true
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -Transaction

Caller-owned active transaction. It must belong to the supplied connection when both are present.

```yaml
Type: Ahtola.Data.Sqlite.SqliteTransaction
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

### -Where

Dictionary of column names and values used to select rows. Omit only to intentionally operate on all rows.

```yaml
Type: System.Collections.IDictionary
DefaultValue: ''
SupportsWildcards: false
Aliases:
- ClauseData
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

### Ahtola.Data.Sqlite.SqliteConnection

A value can be piped to the pipeline-enabled parameter.

## OUTPUTS

### System.Int32

The number of rows deleted.

## NOTES

Supports `-WhatIf` and `-Confirm`. Omitting `Where` deletes every row in the table.

## RELATED LINKS

[PowerShell module guide](https://github.com/Devolutions/ahtola/blob/master/docs/powershell-module.md)
