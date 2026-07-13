-- DESIGN.md §20.4 corpus fixture: p19_quoted_identifier_off_module
-- M4 scope: §11.2 push-time environment fetch -- `sys.sql_modules.uses_quoted_identifier`
-- is read for the CALLEE at push time, independent of the caller's own frame env. The
-- callee here is created with QUOTED_IDENTIFIER OFF (so a double-quoted string is a
-- STRING LITERAL, not a delimited identifier); the caller is ON (default). Both must
-- parse and execute correctly using their OWN module's setting.
SET QUOTED_IDENTIFIER OFF;
GO
CREATE OR ALTER PROCEDURE dbo.p19_callee_qi_off
    @Out nvarchar(50) OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET @Out = "hello";
END
GO
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE dbo.p19_quoted_identifier_off_module
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Result nvarchar(50);
    EXEC dbo.p19_callee_qi_off @Out = @Result OUTPUT;
    SELECT @Result AS Result;
END
GO
