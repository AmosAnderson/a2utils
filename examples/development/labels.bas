REM Symbolic source: run basic prepare before basic compile.
@start: HOME
FOR I=1 TO 3
GOSUB @show
NEXT I
GOTO @done

@show:
PRINT "HELLO ";I
RETURN

@done: END
