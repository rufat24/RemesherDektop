#!/bin/bash
# summ.sh file-prefix... : one row per run
for f in "$@"; do
  l=$f.log
  m=$(python qrmetric.py $f.obj 2>/dev/null | grep -o 'faces=[0-9]*\|p05=[0-9.]*\|edgeCV=[0-9.]*' | tr '\n' ' ')
  g=$(grep -o 'unsat=[0-9]*\|flips=[0-9]*\|irregular=[^ ]*\|open=[0-9]*\|hausdorff=[^ ]*\|totalMs=[0-9]*' $l | tail -6 | tr '\n' ' ')
  q=$(grep -o 'orth mean=[0-9.]*' $l)
  echo "$(basename $f) $m $q $g"
done
