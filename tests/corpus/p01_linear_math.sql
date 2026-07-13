-- DESIGN.md §20.4 corpus fixture: p01_linear_math
-- CREATE OR ALTER (SQL Server 2016 SP1+) so the fidelity harness can deploy it
-- idempotently into a scratch database without pre-drop logic.
CREATE OR ALTER PROCEDURE dbo.p01_linear_math
    @A int,
    @B int
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Sum int = @A + @B;
    DECLARE @Product int = @A * @B;
    SELECT @Sum AS Sum, @Product AS Product;
END
