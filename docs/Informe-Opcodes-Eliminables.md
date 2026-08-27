# Informe: evaluacion de eliminabilidad de opcodes de Surtr

**Fecha:** 2026-08-27 Â· **Estado:** investigacion con medicion de calor dinamico.
**Metodo de medicion:** el despacho de `SurtrVirtualMachine.Run()` se instrumento en un worktree
desechable con un contador por opcode (`DispatchCounts[*ip]++` en la etiqueta `Dispatch`, que mide
cada instruccion justo antes de ejecutarse) y el contador se vacio al `ProcessExit`. Se ejecuto la
suite completa del bench (`--verify-only --surtr-only`, 50 workloads, tamano completo, checksums
verificados). Total medido: **296 477 444 instrucciones ejecutadas**. La emision estatica (que
opcodes emite el compilador en el modulo del bench) se conto instrumentando los puntos de escritura
del `SurtrCodeEmitter` (`Emit`, `Simple`, `WithU8/16/32`, `Branch`).

**Limites de la medicion:**
- El calor dinamico es de la **suite del bench** (50 workloads + stdlib que estos ejercen). Un
  opcode con 0 ejecuciones aqui no esta muerto: puede ser necesario para el lenguaje y ejercitarlo
  solo la stdlib en rutas que la suite no cubre (p. ej. `FSub`, `And`, `Xor` no aparecen).
- La emision estatica solo cubre el modulo del bench compilado en el proceso; la stdlib se carga de
  imagenes precompiladas y no pasa por el contador.
- Para decidir eliminacion, el calor se combina con: (a) si el compilador tiene metodo de emision
  para el opcode (los 242 metodos de `SurtrCodeEmitter.OpCodes.cs`), y (b) si tiene alternativa.

> **Nota posterior (2026-08-28).** Este informe es la foto de calor con la que se decidio; el
> juego de instrucciones ha cambiado desde entonces. Los 39 gemelos anchos que la seccion 3 lista
> ya no existen: el prefijo `Wide` (`0xF0`) los sustituye a todos, el juego primario tiene **203
> opcodes** y `SurtrModuleImage.FormatVersion` esta en **14**. `docs/Informe-Opcodes-Layout.md`
> mide el resultado y corrige ocho entradas de la tabla de abajo — entre ellas `ArrNewX`, que **no**
> es un gemelo ancho, e `InvokeSpecial`, que no tiene alternativa. Las cifras de calor siguen siendo
> validas: son de la suite, no del juego de opcodes.

## Resumen ejecutivo

1. **240 opcodes primarios + 13 ext opcodes** (espacio `Ext`). De los 240 primarios, **100 se
   ejecutan** en la suite y **140 no se ejecutan en absoluto** (0 veces). De los 13 ext, todos los
   `*ForNext` son superinstrucciones de bucle medidas y validadas.

2. **Distribucion extremadamente desigual (Pareto).** Los **12 opcodes mas calientes concentran el
   60,7 % de todas las ejecuciones**: Ldl2, Ldl1, Ldl0, Add, JP, JPGE, IncLocal, PushI8, Mod,
   PushI32, Ldl3, LdlS. Los 20 primeros superan el 70 %. La presion de registros y el footprint del
   bucle caliente viven en un conjunto de ~20 opcodes.

3. **Los candidatos a "eliminacion" no son opcodes sueltos: son gemelos y especializaciones.** La
   mitad del espacio son pares `X` (immediato de 4 bytes) de un opcode de 2 bytes (p. ej.
   `JPX`/`JP`, `LdcX`/`Ldc`, `CastX`/`Cast`, `InvokeStaticX`/`InvokeStatic`, `ObjNewX`/`ObjNew`,
   `InstanceOfX`/`InstanceOf`, `LoadTypeX`/`LoadType`, los 22 saltos `JP*X`, `StaticFieldGetX`,
   `NewClosureX`, `NewFunctionX`, `BoxAsX`, `CastOrNullX`, `CallModuleX`, `CallLocalModuleX`).
   Esos ~30 pares son **eliminables por fusion con su forma corta** (el offset/immediato de 4
   bytes puede ampliarse en el emisor como relajacion, que ya se hace para los saltos). No requieren
   cambiar el formato, solo que el emisor use la forma corta siempre y reserve la `X` como
   relajacion, o los retire.

4. **Los Ldc0-9 (10 opcodes) son fusionables en `LdcS`** (1 byte de indice). Los `Ldl0-5`/`Stl0-5`
   (12 opcodes) en `LdlS`/`StlS`. Estas especializaciones de indice fijo ahorran un byte y un
   dispatch al opcode generico; su eliminacion real es **fusion en el compilador** (que ya las
   elige), no en el formato. Puntuacion 40-70: se mantienen porque son el caso comun, se eliminan
   como *forma* si el emisor siempre puede usar el generico (coste: +1 byte por uso).

5. **Opcodes puros redundantes** (alternativa directa, sin uso en suite): `PushNull` (PushI32+tag),
   `PushTrue`/`PushFalse` (PushI8+tag), `Neg` (Sub de 0), `FNeg`, `Not` (Xor -1), `Inv` (Xor 1),
   `TupGet` (el compilador siempre emite `TupGetC`), `ArrIn` (ArrIndexOf+GE 0), `DictIn`
   (DictGet+IsPresent), `RangePack`/`RangeUnpack` (BoxValue/UnboxValue), `GenIterate` (check de
   State inline). Eliminar estas **ahorra 12 valores del espacio primario** sin perder lenguaje.

6. **Nop** se emite como placeholder de parcheo de saltos (`SurtrCodeEmitter.Helpers` escribe
   `OpCode.Nop` como `Current`/`Wide` en relocaciones). No se ejecuto ni una vez en la suite (0
   ejecuciones), lo que indica que el parcheo siempre lo reescribe. Su eliminacion depende de que el
   parcheo deje de necesitarlo; hoy no cuesta nada.

7. **La suma de lo eliminable**: ~30 pares `X` + 10 `LdcN` + 12 `LdlN`/`StlN` + ~12 redundantes
   **no reducen el numero de dispatchs del bucle caliente** (los calientes no estan en esa lista).
   El beneficio de eliminar opcodes es **espacio de formato y simplicidad**, no velocidad: el
   rendimiento lo dominan ~20 opcodes que son irremplazables.

8. **Donde SÃ hay velocidad por fusion** (la via real): los opcodes calientes se agrupan en
   **patrones de bucle fusionables** â€” `Ldl* + Add + Stl*` (acumulador), `IncLocal + JPGE`
   (contador de bucle), `PushI8 1 + Add + Stl` (incremento), `ArrGet` en `for-in`. El espacio `Ext`
   ya fusiona `for-in` (ArrForNext/StrForNext/TupForNext/DictForNext/ForRangeNext). **La siguiente
   fusion con retorno medible** es un `IncLocal + branch condicional` combinado (el contador de
   `for i in 0..n` actual son `IncLocal` + `PushI32 n` + `Ldl` + `JPGE` â‰ˆ 4 dispatches por
   iteracion, y un `ForCountedNext` los haria 1). Ver recomendaciones.

## La tabla

Columnas: **ID** (valor del opcode) Â· **Nombre** Â· **Region** Â· **Que hace** Â· **Coste** del cuerpo
en `Run()` (B=barato, M=medio, A=alto: allocacion/llamada) Â· **Emit** (veces que el compilador lo
emite en el modulo del bench) Â· **Exec** (ejecuciones en la suite de 50 workloads) Â· **%** del total
de 296 M Â· **Alternativa** Â· **Elim** (puntuacion de eliminabilidad 0-100).

> **Lectura de la puntuacion Elim:** 0-15 = imprescindible (caliente, sin alternativa); 16-35 =
> mantener (necesario o caliente); 36-60 = fusionable/reescribible (el compilador podria evitar
> emitirlo); 61-85 = eliminable (alternativa directa o forma redundante); 86-100 = muerto (sin
> emision, sin ejecucion, sin uso de lenguaje). Una puntuacion alta NO significa "borrarlo ya":
> significa "el formato puede prescindir de el".

## Opcodes del espacio primario

| ID | Nombre | Region | Que hace | Coste | Emit | Exec | % | Alternativa | Elim |
|---|---|---|---|---|---|---|---|---|---|
| 0x00 | Nop | Stack | No-op (parcheo/relleno de saltos) | B |  | 0 |  | Eliminar si el parcheo deja de usarlo | 80 |
| 0x01 | Dup | Stack | Duplica el top | B | 15 | 1608008 | 0,54 | Ninguna; base del stack machine | 5 |
| 0x02 | Pop | Stack | Descarta el top (sobra de llamada) | B | 1 | 276022 | 0,09 | Invoke con retCount=0 ya lo hace | 10 |
| 0x03 | PushNull | Const | Push null reference | B |  | 0 |  | PushI32 0 + TagMaskReference | 55 |
| 0x04 | PushTrue | Const | Push true | B | 1 | 50000 | 0,02 | PushI8 1 + tag | 45 |
| 0x05 | PushFalse | Const | Push false | B | 1 | 1 | 0,00 | PushI8 0 + tag | 45 |
| 0x06 | PushI8 | Const | PushI8 (literal 1B) | B |  | 13039140 | 4,40 | Ninguna; caso comun de literal | 5 |
| 0x07 | PushI16 | Const | PushI16 (literal 2B) | B |  | 380257 | 0,13 | PushI32 | 25 |
| 0x08 | PushI32 | Const | PushI32 (literal 4B) | B | 52 | 10605000 | 3,58 | Ninguna | 10 |
| 0x09 | PushChar | Const | PushChar (literal) | B |  | 0 |  | PushI16 + tag | 20 |
| 0x0A | PushAbsent | Const | PushAbsent (nullable sin valor) | B | 3 | 100000 | 0,03 | PushI8 + tag | 35 |
| 0x0B | Ldc0 | Const | Ldc0 (constante 0) | B | 2 | 0 |  | LdcS | 70 |
| 0x0C | Ldc1 | Const | Ldc1 (constante 1) | B | 2 | 0 |  | LdcS | 70 |
| 0x0D | Ldc2 | Const | Ldc2 (constante 2) | B | 2 | 0 |  | LdcS | 70 |
| 0x0E | Ldc3 | Const | Ldc3 (constante 3) | B | 1 | 0 |  | LdcS | 70 |
| 0x0F | Ldc4 | Const | Ldc4 (constante 4) | B | 1 | 0 |  | LdcS | 70 |
| 0x10 | Ldc5 | Const | Ldc5 (constante 5) | B | 2 | 100001 | 0,03 | LdcS | 70 |
| 0x11 | Ldc6 | Const | Ldc6 (constante 6) | B | 1 | 1000000 | 0,34 | LdcS | 70 |
| 0x12 | Ldc7 | Const | Ldc7 (constante 7) | B | 13 | 3000004 | 1,01 | LdcS | 70 |
| 0x13 | Ldc8 | Const | Ldc8 (constante 8) | B | 1 | 64 | 0,00 | LdcS | 70 |
| 0x14 | Ldc9 | Const | Ldc9 (constante 9) | B | 1 | 1 | 0,00 | LdcS | 70 |
| 0x15 | LdcS | Const | LdcS (indice 1B) | B | 28 | 2009214 | 0,68 | Ninguna; caso comun del pool | 15 |
| 0x16 | Ldc | Const | Ldc (indice 2B) | B |  | 0 |  | Ninguna | 20 |
| 0x17 | LdcX | Const | LdcX (indice 4B) | B |  | 0 |  | Ldc; pools >64K rarisimos | 60 |
| 0x18 | Ldl0 | Locales | Ldl0 (carga el local 0) | B | 129 | 21419474 | 7,22 | LdlS | 40 |
| 0x19 | Ldl1 | Locales | Ldl1 (carga el local 1) | B | 155 | 22582932 | 7,62 | LdlS | 40 |
| 0x1A | Ldl2 | Locales | Ldl2 (carga el local 2) | B | 129 | 22829971 | 7,70 | LdlS | 40 |
| 0x1B | Ldl3 | Locales | Ldl3 (carga el local 3) | B | 67 | 10255088 | 3,46 | LdlS | 40 |
| 0x1C | Ldl4 | Locales | Ldl4 (carga el local 4) | B | 44 | 4395257 | 1,48 | LdlS | 40 |
| 0x1D | Ldl5 | Locales | Ldl5 (carga el local 5) | B | 19 | 2390030 | 0,81 | LdlS | 40 |
| 0x1E | LdlS | Locales | LdlS (indice 1B) | B | 44 | 8609672 | 2,90 | Ninguna; caso comun | 10 |
| 0x1F | Ldl | Locales | Ldl (indice 2B) | B |  | 0 |  | Ninguna | 15 |
| 0x20 | Stl0 | Locales | Stl0 (almacena el local 0) | B |  | 0 |  | StlS | 40 |
| 0x21 | Stl1 | Locales | Stl1 (almacena el local 1) | B | 86 | 8201253 | 2,77 | StlS | 40 |
| 0x22 | Stl2 | Locales | Stl2 (almacena el local 2) | B | 70 | 4250049 | 1,43 | StlS | 40 |
| 0x23 | Stl3 | Locales | Stl3 (almacena el local 3) | B | 55 | 4338026 | 1,46 | StlS | 40 |
| 0x24 | Stl4 | Locales | Stl4 (almacena el local 4) | B | 28 | 1975031 | 0,67 | StlS | 40 |
| 0x25 | Stl5 | Locales | Stl5 (almacena el local 5) | B | 20 | 1600021 | 0,54 | StlS | 40 |
| 0x26 | StlS | Locales | StlS (indice 1B) | B | 27 | 2838851 | 0,96 | Ninguna; caso comun | 10 |
| 0x27 | Stl | Locales | Stl (indice 2B) | B |  | 0 |  | Ninguna | 15 |
| 0x28 | IncLocal | Locales | IncLocal (i+=k en sitio) | B |  | 13342584 | 4,50 | Fusion ya hecha (Ldl+Push+Add+Stl) | 20 |
| 0x29 | FieldGet | Campos | FieldGet (campo de instancia) | B | 27 | 5925002 | 2,00 | Ninguna; acceso mas comun | 5 |
| 0x2A | FieldSet | Campos | FieldSet | B | 15 | 3550012 | 1,20 | Ninguna | 5 |
| 0x2B | StaticFieldGet | Campos | StaticFieldGet | M | 14 | 800000 | 0,27 | FieldGet contra tabla estatica | 25 |
| 0x2C | StaticFieldGetX | Campos | StaticFieldGetX (4B) | M |  | 0 |  | StaticFieldGet | 65 |
| 0x2D | StaticFieldSet | Campos | StaticFieldSet | M | 3 | 3 | 0,00 | FieldSet contra tabla estatica | 25 |
| 0x2E | StaticFieldSetX | Campos | StaticFieldSetX (4B) | M |  | 0 |  | StaticFieldSet | 65 |
| 0x2F | UpValueGet | Upvalue | UpValueGet (captura de closure) | B | 3 | 900000 | 0,30 | InvokeClosure + slot | 45 |
| 0x30 | LoadValueLocal | ValueType | LoadValueLocal (bloque multi-slot) | M |  | 4800002 | 1,62 | Ldl x N; fusion en bucle | 30 |
| 0x31 | StoreValueLocal | ValueType | StoreValueLocal | M |  | 4500002 | 1,52 | Stl x N | 30 |
| 0x32 | LoadLocalField | ValueType | LoadLocalField (slot a offset) | M |  | 7300000 | 2,46 | Ldl + FieldGet | 30 |
| 0x33 | StoreLocalField | ValueType | StoreLocalField | M |  | 0 |  | Stl + FieldSet | 30 |
| 0x34 | LoadValueField | ValueType | LoadValueField (bloque de campo) | M |  | 1200000 | 0,40 | FieldGet x N | 35 |
| 0x35 | StoreValueField | ValueType | StoreValueField | M |  | 300002 | 0,10 | FieldSet x N | 35 |
| 0x36 | LoadValueStatic | ValueType | LoadValueStatic | M |  | 0 |  | StaticFieldGet x N | 35 |
| 0x37 | StoreValueStatic | ValueType | StoreValueStatic | M |  | 0 |  | StaticFieldSet x N | 35 |
| 0x38 | BoxValue | ValueType | BoxValue (box de value type) | A |  | 0 |  | ObjNew + copia de slots | 20 |
| 0x39 | UnboxValue | ValueType | UnboxValue | A |  | 0 |  | Unbox + copia de slots | 20 |
| 0x3A | RangePack | Rangos | RangePack (box de rango) | A |  | 0 |  | BoxValue | 40 |
| 0x3B | RangeUnpack | Rangos | RangeUnpack | A |  | 0 |  | UnboxValue | 40 |
| 0x3C | Add | Aritmetica | Add int | B | 75 | 15230054 | 5,14 | Ninguna; el opcode mas caliente | 0 |
| 0x3D | FAdd | Aritmetica | FAdd | B | 17 | 5900000 | 1,99 | Ninguna | 5 |
| 0x3E | Sub | Aritmetica | Sub int | B | 10 | 737584 | 0,25 | Ninguna | 5 |
| 0x3F | FSub | Aritmetica | FSub | B |  | 0 |  | Ninguna | 10 |
| 0x40 | Mul | Aritmetica | Mul int | B | 10 | 1590000 | 0,54 | Ninguna | 5 |
| 0x41 | FMul | Aritmetica | FMul | B | 21 | 7000000 | 2,36 | Ninguna | 5 |
| 0x42 | Div | Aritmetica | Div int | M | 1 | 300000 | 0,10 | Ninguna (trap div-0) | 10 |
| 0x43 | FDiv | Aritmetica | FDiv | M |  | 0 |  | Ninguna | 10 |
| 0x44 | Mod | Aritmetica | Mod int | M | 63 | 12985000 | 4,38 | Ninguna; muy caliente en la suite | 10 |
| 0x45 | FMod | Aritmetica | FMod | M |  | 0 |  | Ninguna | 20 |
| 0x46 | Neg | Aritmetica | Neg int | B |  | 0 |  | Sub de 0 | 35 |
| 0x47 | FNeg | Aritmetica | FNeg | B |  | 0 |  | FSub de 0 | 40 |
| 0x48 | And | Bitwise | And | B |  | 0 |  | Ninguna | 15 |
| 0x49 | Or | Bitwise | Or | B |  | 0 |  | Ninguna | 15 |
| 0x4A | Xor | Bitwise | Xor | B |  | 0 |  | Ninguna | 15 |
| 0x4B | Not | Bitwise | Not (complemento) | B |  | 0 |  | Xor con -1 | 35 |
| 0x4C | Shl | Bitwise | Shl | B |  | 0 |  | Ninguna | 15 |
| 0x4D | Shr | Bitwise | Shr (logico) | B |  | 0 |  | Ninguna | 15 |
| 0x4E | Sar | Bitwise | Sar (aritmetico) | B |  | 0 |  | Ninguna | 15 |
| 0x4F | Inv | Bitwise | Inv (negacion booleana) | B | 1 | 300000 | 0,10 | Xor con 1 | 45 |
| 0x50 | EQ | Compara | EQ int | B | 4 | 800000 | 0,27 | JPEQ fija; EQ queda para booleanos | 20 |
| 0x51 | NE | Compara | NE | B |  | 0 |  | JPNE fija | 20 |
| 0x52 | GT | Compara | GT | B | 2 | 0 |  | JPGT fija | 20 |
| 0x53 | GE | Compara | GE | B | 1 | 300000 | 0,10 | JPGE fija | 20 |
| 0x54 | LT | Compara | LT | B | 3 | 276022 | 0,09 | JPLT fija | 20 |
| 0x55 | LE | Compara | LE | B | 1 | 268768 | 0,09 | JPLE fija | 20 |
| 0x56 | FEQ | Compara | FEQ | B |  | 0 |  | JPFEQ fija | 20 |
| 0x57 | FNE | Compara | FNE | B |  | 0 |  | JPFNE fija | 20 |
| 0x58 | FGT | Compara | FGT | B |  | 0 |  | JPFGT fija | 20 |
| 0x59 | FGE | Compara | FGE | B |  | 0 |  | JPFGE fija | 20 |
| 0x5A | FLT | Compara | FLT | B |  | 0 |  | JPFLT fija | 20 |
| 0x5B | FLE | Compara | FLE | B |  | 0 |  | JPFLE fija | 20 |
| 0x5C | REQ | Compara | REQ (identidad de ref) | B |  | 0 |  | JPREQ fija | 20 |
| 0x5D | RNE | Compara | RNE | B |  | 0 |  | JPRNE fija | 20 |
| 0x5E | StrEQ | Compara | StrEQ (texto) | M |  | 0 |  | StrHash+Switch+StrEQ | 25 |
| 0x5F | StrNE | Compara | StrNE | M |  | 0 |  | JPStrNE fija | 25 |
| 0x60 | DynEQ | Compara | DynEQ (por tag; genericos) | M |  | 0 |  | SurtrValueComparer | 30 |
| 0x61 | DynNE | Compara | DynNE | M |  | 0 |  | SurtrValueComparer | 30 |
| 0x62 | IsNull | Null | IsNull | B |  | 0 |  | JPN fija | 25 |
| 0x63 | IsNotNull | Null | IsNotNull | B |  | 0 |  | JPNN fija | 25 |
| 0x64 | IsAbsent | Null | IsAbsent | B |  | 0 |  | JPA fija | 25 |
| 0x65 | IsPresent | Null | IsPresent | B | 1 | 300000 | 0,10 | JPNA fija | 25 |
| 0x66 | I2F | Conv | I2F (int a float) | B | 3 | 900000 | 0,30 | Ninguna (cvtsi2sd) | 10 |
| 0x67 | F2I | Conv | F2I (satura a int) | M |  | 0 |  | Ninguna (determinismo ARM/x64) | 20 |
| 0x68 | I2C | Conv | I2C (retag a char) | B |  | 0 |  | Ninguna | 15 |
| 0x69 | C2I | Conv | C2I | B |  | 0 |  | Ninguna | 15 |
| 0x6A | I2B | Conv | I2B (a bool normalizado) | B |  | 0 |  | Ninguna (normaliza a 0/1) | 15 |
| 0x6B | B2I | Conv | B2I | B | 4 | 0 |  | Ninguna | 15 |
| 0x6C | BoxInt | Conv | BoxInt | A | 3 | 350000 | 0,12 | BoxDynamic con tag conocido | 35 |
| 0x6D | BoxFloat | Conv | BoxFloat | A |  | 0 |  | BoxDynamic | 35 |
| 0x6E | BoxBool | Conv | BoxBool | A |  | 0 |  | BoxDynamic | 35 |
| 0x6F | BoxChar | Conv | BoxChar | A |  | 0 |  | BoxDynamic | 35 |
| 0x70 | BoxAs | Conv | BoxAs (value class a ref) | A |  | 0 |  | ObjNew + copia | 25 |
| 0x71 | BoxAsX | Conv | BoxAsX (4B) | A |  | 0 |  | BoxAs | 60 |
| 0x72 | Unbox | Conv | Unbox | A | 4 | 0 |  | Lectura de Boxed.Value | 25 |
| 0x73 | BoxDynamic | Conv | BoxDynamic (por tag) | A |  | 0 |  | Ninguna; genericos erasa | 20 |
| 0x74 | UnboxDynamic | Conv | UnboxDynamic | A | 8 | 650000 | 0,22 | Ninguna; contraparte | 20 |
| 0x75 | RangeNew | Rangos | RangeNew (bloque 2 bound + excl) | B |  | 0 |  | PushFalse tras bound | 35 |
| 0x76 | RangeNewInclusive | Rangos | RangeNewInclusive | B |  | 0 |  | PushTrue tras bound | 35 |
| 0x77 | StrLen | Strings | StrLen | B | 5 | 600001 | 0,20 | Ninguna | 10 |
| 0x78 | StrGet | Strings | StrGet (char a indice) | M |  | 0 |  | Ninguna (bounds trap) | 15 |
| 0x79 | StrCat | Strings | StrCat (N en una) | A | 4 | 101264 | 0,03 | Fusion ya hecha; necesaria | 15 |
| 0x7A | StrHash | Strings | StrHash (hash cacheado) | M |  | 0 |  | Necesaria para switch de strings | 20 |
| 0x7B | ArrNew | Arrays | ArrNew (alloc) | A | 1 | 1 | 0,00 | Ninguna | 15 |
| 0x7C | ArrNewX | Arrays | ArrNewX (longitud inmediata) | A |  | 0 |  | ArrNew | 55 |
| 0x7D | ArrPack | Arrays | ArrPack (literal) | A |  | 8 | 0,00 | ArrNew + ArrSet x N | 30 |
| 0x7E | ArrLen | Arrays | ArrLen | B | 5 | 115005 | 0,04 | Ninguna | 10 |
| 0x7F | ArrGet | Arrays | ArrGet | B | 13 | 2152600 | 0,73 | Ninguna; muy caliente | 5 |
| 0x80 | ArrSet | Arrays | ArrSet | B | 4 | 900000 | 0,30 | Ninguna; caliente | 5 |
| 0x81 | ArrPush | Arrays | ArrPush | M | 8 | 215320 | 0,07 | Ninguna; el descriptor no es expresable | 20 |
| 0x82 | ArrPop | Arrays | ArrPop | M |  | 0 |  | Ninguna | 25 |
| 0x83 | ArrInsert | Arrays | ArrInsert | A |  | 0 |  | Ninguna | 30 |
| 0x84 | ArrRemoveAt | Arrays | ArrRemoveAt | A |  | 0 |  | Ninguna | 30 |
| 0x85 | ArrClear | Arrays | ArrClear | M |  | 0 |  | Ninguna | 30 |
| 0x86 | ArrIndexOf | Arrays | ArrIndexOf | A |  | 0 |  | Ninguna; scan lineal | 35 |
| 0x87 | ArrIn | Arrays | ArrIn | A |  | 0 |  | ArrIndexOf + GE 0 | 35 |
| 0x88 | TupPack | Tuplas | TupPack | A |  | 0 |  | Ninguna | 20 |
| 0x89 | TupUnpack | Tuplas | TupUnpack | A |  | 0 |  | TupGetC x N | 35 |
| 0x8A | TupLen | Tuplas | TupLen | B |  | 0 |  | Ninguna | 20 |
| 0x8B | TupGet | Tuplas | TupGet (indice en pila) | A |  | 0 |  | TupGetC (indice constante) | 50 |
| 0x8C | TupGetC | Tuplas | TupGetC (indice inmediato) | A |  | 0 |  | Ninguna; el caso comun | 20 |
| 0x8D | DictNew | Dict | DictNew (alloc) | A |  | 0 |  | Ninguna | 15 |
| 0x8E | DictPack | Dict | DictPack (literal) | A |  | 4 | 0,00 | DictNew + DictSet x N | 30 |
| 0x8F | DictLen | Dict | DictLen | B |  | 0 |  | Ninguna | 15 |
| 0x90 | DictGet | Dict | DictGet | M | 3 | 360000 | 0,12 | Ninguna; caliente en dict | 10 |
| 0x91 | DictSet | Dict | DictSet | M | 4 | 110064 | 0,04 | Ninguna | 10 |
| 0x92 | DictDel | Dict | DictDel | M | 1 | 30000 | 0,01 | Ninguna | 25 |
| 0x93 | DictClear | Dict | DictClear | M |  | 0 |  | Ninguna | 30 |
| 0x94 | DictKeys | Dict | DictKeys (alloc) | A | 1 | 1 | 0,00 | Ninguna | 30 |
| 0x95 | DictValues | Dict | DictValues (alloc) | A |  | 0 |  | Ninguna | 30 |
| 0x96 | DictIn | Dict | DictIn | M | 1 | 30000 | 0,01 | DictGet + IsPresent | 25 |
| 0x97 | InstanceOf | Tipos | InstanceOf | M |  | 0 |  | JPInstanceOf fija | 20 |
| 0x98 | InstanceOfX | Tipos | InstanceOfX (4B) | M |  | 0 |  | InstanceOf | 60 |
| 0x99 | Cast | Tipos | Cast (checked) | M |  | 0 |  | Ninguna; trap InvalidCast | 15 |
| 0x9A | CastX | Tipos | CastX (4B) | M |  | 0 |  | Cast | 60 |
| 0x9B | CastOrNull | Tipos | CastOrNull (as?) | M | 1 | 300000 | 0,10 | Cast en try/catch | 30 |
| 0x9C | CastOrNullX | Tipos | CastOrNullX (4B) | M |  | 0 |  | CastOrNull | 60 |
| 0x9D | LoadType | Tipos | LoadType (typeof estatico) | M |  | 0 |  | Ninguna; cache | 20 |
| 0x9E | LoadTypeX | Tipos | LoadTypeX (4B) | M |  | 0 |  | LoadType | 60 |
| 0x9F | GetTypeOfValue | Tipos | GetTypeOfValue (typeof dinamico) | M |  | 0 |  | Ninguna | 25 |
| 0xA0 | LoadModule | Modulos | LoadModule | M |  | 0 |  | Ninguna; cache | 25 |
| 0xA1 | LoadModuleX | Modulos | LoadModuleX (4B) | M |  | 0 |  | LoadModule | 60 |
| 0xA2 | LoadCurrentModule | Modulos | LoadCurrentModule | M |  | 0 |  | LoadModule del chunk propio | 35 |
| 0xA3 | ObjNew | Objetos | ObjNew (alloc; caliente) | A | 17 | 1308011 | 0,44 | Ninguna | 10 |
| 0xA4 | ObjNewX | Objetos | ObjNewX (4B) | A |  | 0 |  | ObjNew | 55 |
| 0xA5 | JP | Saltos | JP (incondicional) | B | 93 | 14849417 | 5,01 | Ninguna; muy caliente | 5 |
| 0xA6 | JPX | Saltos | JPX (4B) | B |  | 0 |  | JP (relajacion de offset) | 55 |
| 0xA7 | JPZ | Saltos | JPZ (branch if false) | B | 15 | 2360006 | 0,80 | Ninguna; caliente | 5 |
| 0xA8 | JPZX | Saltos | JPZX (4B) | B |  | 0 |  | JPZ | 55 |
| 0xA9 | JPNZ | Saltos | JPNZ (branch if true) | B |  | 0 |  | JPZ con condicion invertida | 25 |
| 0xAA | JPNZX | Saltos | JPNZX (4B) | B |  | 0 |  | JPNZ | 55 |
| 0xAB | JPN | Saltos | JPN (branch if null) | B | 1 | 300000 | 0,10 | JPZ + IsNull | 25 |
| 0xAC | JPNX | Saltos | JPNX (4B) | B |  | 0 |  | JPN | 55 |
| 0xAD | JPNN | Saltos | JPNN (branch if not null) | B |  | 0 |  | JPZ + IsNotNull | 25 |
| 0xAE | JPNNX | Saltos | JPNNX (4B) | B |  | 0 |  | JPNN | 55 |
| 0xAF | JPA | Saltos | JPA (branch if absent) | B |  | 0 |  | JPZ + IsAbsent | 25 |
| 0xB0 | JPAX | Saltos | JPAX (4B) | B |  | 0 |  | JPA | 55 |
| 0xB1 | JPNA | Saltos | JPNA (branch if present) | B |  | 0 |  | JPZ + IsPresent | 25 |
| 0xB2 | JPNAX | Saltos | JPNAX (4B) | B |  | 0 |  | JPNA | 55 |
| 0xB3 | JPEQ | Saltos | JPEQ (branch if int =) | B |  | 0 |  | Ninguna; funde cmp+br | 10 |
| 0xB4 | JPEQX | Saltos | JPEQX (4B) | B |  | 0 |  | JPEQ | 55 |
| 0xB5 | JPNE | Saltos | JPNE | B | 9 | 600000 | 0,20 | Ninguna | 10 |
| 0xB6 | JPNEX | Saltos | JPNEX (4B) | B |  | 0 |  | JPNE | 55 |
| 0xB7 | JPGT | Saltos | JPGT | B |  | 0 |  | Ninguna | 10 |
| 0xB8 | JPGTX | Saltos | JPGTX (4B) | B |  | 0 |  | JPGT | 55 |
| 0xB9 | JPGE | Saltos | JPGE | B | 66 | 14174747 | 4,78 | Ninguna; muy caliente | 5 |
| 0xBA | JPGEX | Saltos | JPGEX (4B) | B |  | 0 |  | JPGE | 55 |
| 0xBB | JPLT | Saltos | JPLT | B | 1 | 50001 | 0,02 | Ninguna | 10 |
| 0xBC | JPLTX | Saltos | JPLTX (4B) | B |  | 0 |  | JPLT | 55 |
| 0xBD | JPLE | Saltos | JPLE | B | 4 | 640010 | 0,22 | Ninguna | 10 |
| 0xBE | JPLEX | Saltos | JPLEX (4B) | B |  | 0 |  | JPLE | 55 |
| 0xBF | JPFEQ | Saltos | JPFEQ | B |  | 0 |  | Ninguna | 15 |
| 0xC0 | JPFEQX | Saltos | JPFEQX (4B) | B |  | 0 |  | JPFEQ | 55 |
| 0xC1 | JPFNE | Saltos | JPFNE | B |  | 0 |  | Ninguna | 15 |
| 0xC2 | JPFNEX | Saltos | JPFNEX (4B) | B |  | 0 |  | JPFNE | 55 |
| 0xC3 | JPFGT | Saltos | JPFGT | B |  | 0 |  | Ninguna | 15 |
| 0xC4 | JPFGTX | Saltos | JPFGTX (4B) | B |  | 0 |  | JPFGT | 55 |
| 0xC5 | JPFGE | Saltos | JPFGE | B |  | 0 |  | Ninguna | 15 |
| 0xC6 | JPFGEX | Saltos | JPFGEX (4B) | B |  | 0 |  | JPFGE | 55 |
| 0xC7 | JPFLT | Saltos | JPFLT | B |  | 0 |  | Ninguna | 15 |
| 0xC8 | JPFLTX | Saltos | JPFLTX (4B) | B |  | 0 |  | JPFLT | 55 |
| 0xC9 | JPFLE | Saltos | JPFLE | B |  | 0 |  | Ninguna | 15 |
| 0xCA | JPFLEX | Saltos | JPFLEX (4B) | B |  | 0 |  | JPFLE | 55 |
| 0xCB | JPREQ | Saltos | JPREQ | B |  | 0 |  | Ninguna | 20 |
| 0xCC | JPREQX | Saltos | JPREQX (4B) | B |  | 0 |  | JPREQ | 55 |
| 0xCD | JPRNE | Saltos | JPRNE | B |  | 0 |  | Ninguna | 20 |
| 0xCE | JPRNEX | Saltos | JPRNEX (4B) | B |  | 0 |  | JPRNE | 55 |
| 0xCF | JPStrEQ | Saltos | JPStrEQ | M |  | 0 |  | Ninguna | 20 |
| 0xD0 | JPStrEQX | Saltos | JPStrEQX (4B) | M |  | 0 |  | JPStrEQ | 55 |
| 0xD1 | JPStrNE | Saltos | JPStrNE | M | 4 | 300000 | 0,10 | Ninguna | 20 |
| 0xD2 | JPStrNEX | Saltos | JPStrNEX (4B) | M |  | 0 |  | JPStrNE | 55 |
| 0xD3 | JPInstanceOf | Saltos | JPInstanceOf | M |  | 300000 | 0,10 | Ninguna; funde test+br | 25 |
| 0xD4 | JPInstanceOfX | Saltos | JPInstanceOfX (9B) | M |  | 0 |  | JPInstanceOf | 55 |
| 0xD5 | Switch | Saltos | Switch (tabla densa) | M |  | 300000 | 0,10 | Ninguna | 15 |
| 0xD6 | SwitchLookup | Saltos | SwitchLookup (tabla dispersa) | M |  | 0 |  | Ninguna | 20 |
| 0xD7 | CallLocalModule | Llamadas | CallLocalModule | M |  | 1350055 | 0,46 | Ninguna; el caso comun de llamada | 5 |
| 0xD8 | CallLocalModuleX | Llamadas | CallLocalModuleX (4B) | M |  | 0 |  | CallLocalModule | 55 |
| 0xD9 | CallModule | Llamadas | CallModule (otro modulo) | M |  | 300000 | 0,10 | CallLocalModule + carga de modulo | 30 |
| 0xDA | CallModuleX | Llamadas | CallModuleX (4B; 11B instr) | M |  | 0 |  | CallModule | 55 |
| 0xDB | InvokeVirtual | Llamadas | InvokeVirtual | M |  | 400004 | 0,13 | Ninguna; vtable, caliente | 5 |
| 0xDC | InvokeSpecial | Llamadas | InvokeSpecial (no virtual) | M |  | 5108074 | 1,72 | InvokeVirtual | 30 |
| 0xDD | InvokeStatic | Llamadas | InvokeStatic | M |  | 0 |  | Ninguna; caliente | 5 |
| 0xDE | InvokeStaticX | Llamadas | InvokeStaticX (4B) | M |  | 0 |  | InvokeStatic | 55 |
| 0xDF | InvokeInterface | Llamadas | InvokeInterface | M |  | 400003 | 0,13 | Ninguna; tabla interface | 20 |
| 0xE0 | InvokeClosure | Llamadas | InvokeClosure | M |  | 1468768 | 0,50 | Ninguna; call por stack | 15 |
| 0xE1 | NewClosure | Llamadas | NewClosure (captura) | A |  | 1 | 0,00 | Ninguna | 20 |
| 0xE2 | NewClosureX | Llamadas | NewClosureX (4B) | A |  | 0 |  | NewClosure | 55 |
| 0xE3 | NewFunction | Funciones | NewFunction (canonica 0-captura) | A |  | 300004 | 0,10 | NewClosure sin upvalues | 35 |
| 0xE4 | NewFunctionX | Funciones | NewFunctionX (4B) | A |  | 0 |  | NewFunction | 55 |
| 0xE5 | ReturnVoid | Retorno | ReturnVoid | B | 21 | 1300016 | 0,44 | Ninguna; caliente | 5 |
| 0xE6 | ReturnValue | Retorno | ReturnValue | B | 100 | 5237641 | 1,77 | Ninguna; muy caliente | 5 |
| 0xE7 | ReturnValues | Retorno | ReturnValues (multi-slot) | M |  | 1500000 | 0,51 | ReturnValue x N | 30 |
| 0xE8 | Throw | Excepc | Throw | A | 6 | 8000 | 0,00 | Ninguna | 15 |
| 0xE9 | GenNew | Generador | GenNew (alloc) | A |  | 6 | 0,00 | Ninguna | 15 |
| 0xEA | GenIterate | Generador | GenIterate (check single-use) | B | 3 | 3 | 0,00 | Check de State inline | 40 |
| 0xEB | GenResume | Generador | GenResume (copia de frame) | A | 3 | 150003 | 0,05 | Ninguna | 10 |
| 0xEC | GenCurrent | Generador | GenCurrent | B | 3 | 150000 | 0,05 | Ninguna | 20 |
| 0xED | Yield | Generador | Yield (suspende) | A | 4 | 200000 | 0,07 | Ninguna | 10 |
| 0xEE | GenDelegate | Generador | GenDelegate (yield from) | A | 2 | 2 | 0,00 | Ninguna | 20 |
| 0xEF | GenResumed | Generador | GenResumed (send value) | B | 1 | 50000 | 0,02 | Ninguna | 25 |
| 0xFF | Ext | Prefijo | Ext (abre 2do espacio de opcodes) | M |  | 100002 | 0,03 | Ninguna; mecanismo de fusion | 5 |

## Opcodes extendidos (`SurtrExtOpCode`, tras `Ext = 0xFF`)

Todos son superinstrucciones de bucle: **fusionan un paso entero de `for-in`/`for` en una
instruccion**, y pagan el prefijo (1 dispatch extra) solo porque ahorran 2+ dispatches. Ya validados
por medicion (`docs/Plan-Opcodes-Extendidos.md`). Ninguno es eliminable.

| ID | Nombre | Que fusiona | Dispatchs que ahorra | Elim |
|---|---|---|---|---|
| 0x00 | Probe | Nada (mide el coste del prefijo; el emisor nunca lo emite) | — | 90 (solo de medicion, mantener) |
| 0x01 | ArrForNext | Ldl idx·Ldl src·ArrLen·JPGE·Ldl src·Ldl idx·ArrGet·Stl var·IncLocal·Jump = 10 | 9 | 5 |
| 0x02 | ArrForNextX | igual, offset 4B | 9 | 5 |
| 0x03 | StrForNext | paso de for-in sobre string | ~8 | 5 |
| 0x04 | StrForNextX | igual, offset 4B | ~8 | 5 |
| 0x05 | TupForNext | paso de for-in sobre tupla (boxed) | ~8 | 5 |
| 0x06 | TupForNextX | igual, offset 4B | ~8 | 5 |
| 0x07 | DictForNext | 17 dispatchs de un paso de dict (indice+key+valor+par+salto) | 16 | 5 |
| 0x08 | DictForNextX | igual, offset 4B | 16 | 5 |
| 0x09 | ForRangeNextLE | IncLocal·Ldl·Ldl·JPLE·JP = 5 | 4 | 5 |
| 0x0A | ForRangeNextLEX | igual, offset 4B | 4 | 5 |
| 0x0B | ForRangeNextLT | igual, forma exclusiva | 4 | 5 |
| 0x0C | ForRangeNextLTX | igual, offset 4B | 4 | 5 |

> El calor de `Ext` (0xFF) en la suite fue ~320 000 despachos (todas las superinstrucciones juntas),
> y cayo tras arreglar el contador; las superinstrucciones de `for-in`/`for` son de las mas calientes
> del intérprete cuando el workload es un bucle (forIn, intLoop, vec2Math).

## Recomendaciones priorizadas

**1. Fusion de contador de bucle (alto retorno, el siguiente paso de P4).** El patron mas caliente
del lenguaje es el `for i in 0..n` de conteo: `IncLocal` + `PushI32 n` + `Ldl i` + `JPGE` =
**4 dispatches por iteracion** (y `IncLocal` 13.3 M + `JPGE` 14.2 M + `Ldl` 22 M + `PushI32` 10.6 M
estan todos en el top-10). Una superinstruccion `ForCountedNext varSlot limitSlot offset` (incrementa
el contador y ramifica mientras `<=`) colapsa los 4 en **1 dispatch** — el mismo patron que ya
valido `ForRangeNextLE`, que fusiona exactamente esto para el `for-in` sobre rangos pero no para el
`for` clasico con variable entera. Medible en intLoop/fib: intLoop ejecuta ~4 dispatchs/iteracion de
este patron.

**2. Eliminar los ~30 pares `X` como forma reservada.** Los `JP*X`, `LdcX`, `CastX`, `CastOrNullX`,
`InstanceOfX`, `LoadTypeX`, `StaticFieldGetX/SetX`, `InvokeStaticX`, `ObjNewX`, `NewClosureX`,
`NewFunctionX`, `BoxAsX`, `CallModuleX`, `CallLocalModuleX` pueden quedar como relajacion (el emisor
los usa cuando el inmediato no cabe en 2 bytes) en vez de opcodes nominales. No ahorran dispatchs —
ahorran bytes de formato y simplifican el switch. Si se retiran del enum, hay que re-medir los
modulos viejos (bump de formato), como ya se hizo en el reset contiguo documentado en `OpCode.cs`.

**3. Fusionar los `Ldc0-9`/`Ldl0-5`/`Stl0-5` en sus formas `S`.** Son 22 opcodes para el caso comun.
El compilador ya los elige; se pueden retirar del formato haciendo que el emisor emita siempre
`LdcS`/`LdlS`/`StlS` (coste: +1 byte por uso). La suite los usa muchisimo (Ldl0-3 suman 77 M) — el
coste de +1 byte en el bytecode es el precio; la ganancia es simplicidad de formato, no velocidad.

**4. Retirar los ~12 redundantes** (`PushNull`/`PushTrue`/`PushFalse` como literales, `Neg`/`FNeg`,
`Not`/`Inv`, `TupGet`, `ArrIn`, `DictIn`, `RangePack`/`RangeUnpack`, `GenIterate`): cada uno tiene
una secuencia de 2-3 opcodes genericos que lo sustituye. Ahorra espacio de formato y cases del
switch. Ninguno esta en el top caliente.

**5. No tocar (nunca)**: `Ldl*`/`LdlS`, `Stl*`/`StlS`, `Add`, `Sub`, `Mul`, `Mod`, `JP`, `JPZ`,
`JPGE`, `IncLocal`, `PushI8`, `PushI32`, `ArrGet`, `ArrSet`, `FieldGet`, `FieldSet`, `ReturnValue`,
`ReturnVoid`, `Invoke*`, `CallLocalModule` — los opcodes que concentran el 70 % del trabajo. La
presion de registros de `Run()` se juega ahi, y ya se ataco (informe del commit `ceabd65`).

**6. Prioridad de la puntuacion vs el calor**: la puntuacion mide *eliminabilidad del formato*; el
*retorno de rendimiento* lo dicta la fusion (rec. 1). Eliminar opcodes raros no acelera `Run()`:
reduce el switch (un body menos por opcode) y el espacio de nombres, y libera valores `0xF0-0xFE`
para futuras superinstrucciones sin renumerar.

## Metodo de puntuacion (detalle)

- `0-15`: opcode en el top caliente y sin alternativa (Add, JP, FieldGet, ArrGet, ReturnValue...).
- `16-35`: necesario o con uso real en stdlib/bench (DictGet, StrCat, Yield, InvokeStatic...).
- `36-60`: el compilador podria dejar de emitirlo (PushNull, IsNull con JPN, TupGet con TupGetC,
  los `LdcN` con `LdcS`...).
- `61-85`: forma redundante con alternativa directa, casi siempre el gemelo `X` de 4 bytes, o
  especializacion que el emisor puede generar (Nop, `LdcX`, todos los `JP*X`, `CastX`...).
- `86-100`: sin emision en el compilador, sin ejecucion en la suite, sin uso de lenguaje (hoy
  ninguno llega; `Probe` es el unico por diseño, y se mantiene para medir el prefijo).