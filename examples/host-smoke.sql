-- SqlWorkbench editor test. Only session variables and a read-only SELECT.
use [SampleDb];
declare @sample int=1;
select @sample as sample_value,db_name() as database_name;
