---
document type: cmdlet
external help file: Devolutions.Ahtola.PowerShell.dll-Help.xml
HelpUri: ''
Locale: en-US
Module Name: Devolutions.Ahtola.Sqlite
ms.date: 08/14/2026
PlatyPS schema version: 2024-05-01
title: Get-AhtolaSqliteIndex
---

# Get-AhtolaSqliteIndex

## SYNOPSIS

Lists indexes and their definitions.

## SYNTAX

### __AllParameterSets

```
Get-AhtolaSqliteIndex -Connection <SqliteConnection> [-Table <string>]
```

## ALIASES

This cmdlet has no aliases.

## DESCRIPTION

Queries `sqlite_schema` for indexes. `Table` restricts results to one table; omit it to list all indexes.

## EXAMPLES

### Example 1

This example uses a caller-owned connection and closes it when complete.

```powershell
$connection = New-AhtolaSqliteConnection
try {
    Get-AhtolaSqliteIndex -Connection $connection
} finally { $connection | Close-AhtolaSqliteConnection -Confirm:$false }
```

## PARAMETERS

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
  IsRequired: true
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

### System.Management.Automation.PSCustomObject

PowerShell objects with `name`, `table_name`, and `sql` properties.

## NOTES

The connection is caller-owned and is not closed.

## RELATED LINKS

[PowerShell module guide](https://github.com/Devolutions/ahtola/blob/master/docs/powershell-module.md)
