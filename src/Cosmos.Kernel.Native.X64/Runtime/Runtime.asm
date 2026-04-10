; x64 NativeAOT Runtime Stubs
; EH section accessors and math functions

global get_eh_frame_start
global get_eh_frame_end
global get_dotnet_eh_table_start
global get_dotnet_eh_table_end

extern __eh_frame_start
extern __eh_frame_end
extern __dotnet_eh_table_start
extern __dotnet_eh_table_end

; double math
global cos
global sin
global tan
global pow
global acos
global asin
global atan
global atan2
global exp
global log
global log2
global log10

; float math
global cosf
global sinf
global tanf
global powf
global expf
global logf
global log2f
global log10f

section .text

; =============================================================================
; EH section accessors
; =============================================================================

get_eh_frame_start:
    lea rax, [rel __eh_frame_start]
    ret

get_eh_frame_end:
    lea rax, [rel __eh_frame_end]
    ret

get_dotnet_eh_table_start:
    lea rax, [rel __dotnet_eh_table_start]
    ret

get_dotnet_eh_table_end:
    lea rax, [rel __dotnet_eh_table_end]
    ret

; =============================================================================
; Helper: check if xmm0 is NaN or Inf (exponent == 0x7FF)
; Sets ZF if NaN/Inf. Trashes rax.
; =============================================================================
__check_nan_inf:
    movq rax, xmm0
    shr rax, 52
    and eax, 0x7FF
    cmp eax, 0x7FF
    ret

; =============================================================================
; Double-precision math (x87 FPU)
; =============================================================================

; double sin(double x)
sin:
    call __check_nan_inf
    je .sin_nan
    sub rsp, 8
    movsd [rsp], xmm0
    fld qword [rsp]
    fsin
    fstp qword [rsp]
    movsd xmm0, [rsp]
    add rsp, 8
    ret
.sin_nan:
    movsd xmm0, [rel __math_nan]
    ret

; double cos(double x)
cos:
    call __check_nan_inf
    je .cos_nan
    sub rsp, 8
    movsd [rsp], xmm0
    fld qword [rsp]
    fcos
    fstp qword [rsp]
    movsd xmm0, [rsp]
    add rsp, 8
    ret
.cos_nan:
    movsd xmm0, [rel __math_nan]
    ret

; double tan(double x)
tan:
    call __check_nan_inf
    je .tan_nan
    sub rsp, 8
    movsd [rsp], xmm0
    fld qword [rsp]
    fptan
    fstp st0            ; pop the 1.0 fptan pushes
    fstp qword [rsp]
    movsd xmm0, [rsp]
    add rsp, 8
    ret
.tan_nan:
    movsd xmm0, [rel __math_nan]
    ret

; double asin(double x)
; asin(x) = fpatan(x, sqrt(1 - x*x))
asin:
    ; x == 0 -> return 0 exactly
    xorpd xmm1, xmm1
    ucomisd xmm0, xmm1
    jne .asin_compute
    jp .asin_compute
    ret                             ; xmm0 already 0
.asin_compute:
    sub rsp, 24
    movsd [rsp], xmm0

    movsd xmm1, xmm0
    mulsd xmm1, xmm1               ; x*x
    movsd xmm2, [rel __math_one]
    subsd xmm2, xmm1               ; 1 - x*x
    sqrtsd xmm2, xmm2              ; sqrt(1 - x*x)
    movsd [rsp+8], xmm2

    fld qword [rsp+8]              ; ST0 = sqrt(1-x*x)
    fld qword [rsp]                ; ST0 = x, ST1 = sqrt(1-x*x)
    fpatan                          ; atan2(x, sqrt(1-x*x)) = asin(x)
    fstp qword [rsp]
    movsd xmm0, [rsp]
    add rsp, 24
    ret

; double acos(double x)
; acos(x) = fpatan(sqrt(1 - x*x), x)
acos:
    ; x == 1.0 -> return 0 exactly
    ucomisd xmm0, [rel __math_one]
    jne .acos_compute
    jp .acos_compute
    xorpd xmm0, xmm0
    ret
.acos_compute:
    sub rsp, 24
    movsd [rsp], xmm0

    movsd xmm1, xmm0
    mulsd xmm1, xmm1
    movsd xmm2, [rel __math_one]
    subsd xmm2, xmm1
    sqrtsd xmm2, xmm2
    movsd [rsp+8], xmm2

    fld qword [rsp]                ; ST0 = x
    fld qword [rsp+8]              ; ST0 = sqrt(1-x*x), ST1 = x
    fpatan                          ; atan2(sqrt(1-x*x), x) = acos(x)
    fstp qword [rsp]
    movsd xmm0, [rsp]
    add rsp, 24
    ret

; double atan(double x)
; atan(x) = fpatan(x, 1.0) = atan2(x, 1)
atan:
    ; x == 0 -> return 0 exactly
    xorpd xmm1, xmm1
    ucomisd xmm0, xmm1
    jne .atan_compute
    jp .atan_compute
    ret
.atan_compute:
    sub rsp, 8
    movsd [rsp], xmm0
    fld1                            ; ST0 = 1.0
    fld qword [rsp]                ; ST0 = x, ST1 = 1.0
    fpatan                          ; atan2(x, 1.0) = atan(x)
    fstp qword [rsp]
    movsd xmm0, [rsp]
    add rsp, 8
    ret

; double atan2(double y, double x)
; xmm0 = y, xmm1 = x
atan2:
    ; y == 0 && x > 0 -> return 0 exactly
    xorpd xmm2, xmm2
    ucomisd xmm0, xmm2
    jne .atan2_compute
    jp .atan2_compute
    ; y == 0
    ucomisd xmm1, xmm2
    jbe .atan2_compute              ; x <= 0, need real computation
    xorpd xmm0, xmm0
    ret
.atan2_compute:
    sub rsp, 16
    movsd [rsp], xmm1              ; x
    movsd [rsp+8], xmm0            ; y
    fld qword [rsp]                ; ST0 = x
    fld qword [rsp+8]              ; ST0 = y, ST1 = x
    fpatan                          ; atan2(y, x)
    fstp qword [rsp]
    movsd xmm0, [rsp]
    add rsp, 16
    ret

; double exp(double x)
; exp(x) = 2^(x * log2(e))
exp:
    ; Check NaN/Inf
    movq rax, xmm0
    mov rcx, rax
    shr rcx, 52
    and ecx, 0x7FF
    cmp ecx, 0x7FF
    je .exp_special

    sub rsp, 8
    movsd [rsp], xmm0
    fld qword [rsp]                ; ST0 = x
    fldl2e                          ; ST0 = log2(e), ST1 = x
    fmulp                           ; ST0 = x * log2(e)

    ; 2^ST0: split into int + frac
    fld st0                         ; dup
    frndint                         ; ST0 = int part
    fsub st1, st0                   ; ST1 = frac part
    fxch st1                        ; ST0 = frac, ST1 = int
    f2xm1                           ; ST0 = 2^frac - 1
    fld1
    faddp                           ; ST0 = 2^frac
    fscale                          ; ST0 = 2^frac * 2^int = exp(x)
    fstp st1                        ; pop int
    fstp qword [rsp]
    movsd xmm0, [rsp]
    add rsp, 8
    ret

.exp_special:
    ; rax has the original bits
    bt rax, 63                      ; check sign bit
    jc .exp_neg_inf
    ; +Inf or NaN: check mantissa
    mov rcx, rax
    shl rcx, 12                     ; shift out sign+exponent
    test rcx, rcx
    jnz .exp_nan                    ; non-zero mantissa = NaN
    ; +Inf -> +Inf
    movsd xmm0, [rel __math_pos_inf]
    ret
.exp_neg_inf:
    ; -Inf -> 0
    xorpd xmm0, xmm0
    ret
.exp_nan:
    movsd xmm0, [rel __math_nan]
    ret

; double log(double x) -- natural log
; log(x) = ln(2) * log2(x)
log:
    sub rsp, 8
    movsd [rsp], xmm0
    fld1
    fld qword [rsp]
    fyl2x                           ; log2(x)
    fldln2                          ; ln(2)
    fmulp                           ; ln(2) * log2(x) = ln(x)
    fstp qword [rsp]
    movsd xmm0, [rsp]
    add rsp, 8
    ret

; double log2(double x)
log2:
    sub rsp, 8
    movsd [rsp], xmm0
    fld1
    fld qword [rsp]
    fyl2x                           ; 1.0 * log2(x) = log2(x)
    fstp qword [rsp]
    movsd xmm0, [rsp]
    add rsp, 8
    ret

; double log10(double x)
; log10(x) = log10(2) * log2(x)
log10:
    sub rsp, 8
    movsd [rsp], xmm0
    fld1
    fld qword [rsp]
    fyl2x                           ; log2(x)
    fldlg2                          ; log10(2)
    fmulp                           ; log10(2) * log2(x) = log10(x)
    fstp qword [rsp]
    movsd xmm0, [rsp]
    add rsp, 8
    ret

; double pow(double x, double y)
; xmm0 = x, xmm1 = y
pow:
    sub rsp, 16
    movsd [rsp], xmm0              ; x
    movsd [rsp+8], xmm1            ; y

    ; y == 0 -> return 1.0
    xorpd xmm2, xmm2
    ucomisd xmm1, xmm2
    jne .pow_nonzero_exp
    jp .pow_nonzero_exp
    movsd xmm0, [rel __math_one]
    add rsp, 16
    ret

.pow_nonzero_exp:
    ; x == +Inf -> return +Inf (for y > 0)
    movq rax, xmm0
    mov rcx, 0x7FF0000000000000
    cmp rax, rcx
    je .pow_inf_base

    ; x == 0
    ucomisd xmm0, xmm2
    jne .pow_nonzero_base
    jp .pow_nonzero_base
    ; x == 0: y > 0 -> 0, y < 0 -> +inf
    ucomisd xmm1, xmm2
    ja .pow_ret_zero
    movsd xmm0, [rel __math_pos_inf]
    add rsp, 16
    ret
.pow_ret_zero:
    xorpd xmm0, xmm0
    add rsp, 16
    ret

.pow_nonzero_base:
    ; x < 0?
    ucomisd xmm0, xmm2
    ja .pow_positive_base

    ; Negative base: check if y is integer using FPU (no SSE4.1 roundsd)
    fld qword [rsp+8]              ; ST0 = y
    fld st0                         ; dup y
    frndint                         ; ST0 = round(y)
    fcomip st0, st1                 ; compare round(y) vs y, pop round(y)
    fstp st0                        ; pop y
    jne .pow_ret_nan
    jp .pow_ret_nan

    ; y is integer: negate x, compute, fix sign
    movsd xmm0, xmm0
    movsd xmm3, [rel __math_neg_one]
    mulsd xmm0, xmm3               ; x = -x (positive)
    movsd [rsp], xmm0

    ; Check if y is odd
    cvttsd2si rax, xmm1
    test rax, 1
    jz .pow_even_exp

    ; Odd exponent
    fld qword [rsp+8]
    fld qword [rsp]
    fyl2x
    fld st0
    frndint
    fsub st1, st0
    fxch st1
    f2xm1
    fld1
    faddp
    fscale
    fstp st1
    fchs                            ; negate result
    fstp qword [rsp]
    movsd xmm0, [rsp]
    add rsp, 16
    ret

.pow_even_exp:
    fld qword [rsp+8]
    fld qword [rsp]
    fyl2x
    fld st0
    frndint
    fsub st1, st0
    fxch st1
    f2xm1
    fld1
    faddp
    fscale
    fstp st1
    fstp qword [rsp]
    movsd xmm0, [rsp]
    add rsp, 16
    ret

.pow_inf_base:
    ; x == +Inf: y > 0 -> +Inf, y < 0 -> 0
    ucomisd xmm1, xmm2
    ja .pow_ret_pos_inf
    xorpd xmm0, xmm0
    add rsp, 16
    ret
.pow_ret_pos_inf:
    movsd xmm0, [rel __math_pos_inf]
    add rsp, 16
    ret

.pow_ret_nan:
    movsd xmm0, [rel __math_nan]
    add rsp, 16
    ret

.pow_positive_base:
    ; pow(x,y) = 2^(y * log2(x))
    fld qword [rsp+8]              ; y
    fld qword [rsp]                ; x
    fyl2x                           ; y * log2(x)
    fld st0
    frndint
    fsub st1, st0
    fxch st1
    f2xm1
    fld1
    faddp
    fscale
    fstp st1
    fstp qword [rsp]
    movsd xmm0, [rsp]
    add rsp, 16
    ret

; =============================================================================
; Single-precision float wrappers (promote -> call double -> demote)
; =============================================================================

sinf:
    cvtss2sd xmm0, xmm0
    call sin
    cvtsd2ss xmm0, xmm0
    ret

cosf:
    cvtss2sd xmm0, xmm0
    call cos
    cvtsd2ss xmm0, xmm0
    ret

tanf:
    cvtss2sd xmm0, xmm0
    call tan
    cvtsd2ss xmm0, xmm0
    ret

expf:
    cvtss2sd xmm0, xmm0
    call exp
    cvtsd2ss xmm0, xmm0
    ret

logf:
    cvtss2sd xmm0, xmm0
    call log
    cvtsd2ss xmm0, xmm0
    ret

log2f:
    cvtss2sd xmm0, xmm0
    call log2
    cvtsd2ss xmm0, xmm0
    ret

log10f:
    cvtss2sd xmm0, xmm0
    call log10
    cvtsd2ss xmm0, xmm0
    ret

powf:
    cvtss2sd xmm0, xmm0
    cvtss2sd xmm1, xmm1
    call pow
    cvtsd2ss xmm0, xmm0
    ret

; =============================================================================
; Constants
; =============================================================================
section .rodata
align 16
__math_one:       dq 1.0
__math_neg_one:   dq -1.0
__math_nan:       dq 0x7FF8000000000000
__math_pos_inf:   dq 0x7FF0000000000000
