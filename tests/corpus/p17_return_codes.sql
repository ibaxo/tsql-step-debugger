-- DESIGN.md §20.4 corpus fixture: p17_return_codes
-- M2 scope: RETURN(frame 0) per §22's M2 accept criterion + D9 (ReturnFromFrame,
-- __ret scalar-eval, bare RETURN defaults to 0). Three paths: an explicit negative
-- code, a bare RETURN (implicit 0), and a computed positive code alongside a SELECT
-- that ran before it.
CREATE OR ALTER PROCEDURE dbo.p17_return_codes
    @Mode int
AS
BEGIN
    SET NOCOUNT ON;

    IF @Mode < 0
    BEGIN
        RETURN -1;
    END

    IF @Mode = 0
    BEGIN
        RETURN;                 -- bare RETURN -- defaults to 0
    END

    DECLARE @Computed int = @Mode * 10;
    SELECT @Computed AS Computed;
    RETURN @Computed;
END
