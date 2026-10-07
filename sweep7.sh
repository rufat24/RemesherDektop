U="/c/Program Files/Unity/Hub/Editor/6000.0.58f2/Editor/Unity.exe"
run(){ tag=$1; asset=$2; shift 2; "$U" -batchmode -nographics -projectPath Headless -executeMethod NativeRemesher.Benchmark.EditorParity.Run -asset $asset -extractor IntegerGrid -targets 4600,5000,5400 -tag _$tag "$@" -quit -logFile sw_$tag.log; echo "$tag $(grep -a -m1 "MEAN" sw_$tag.log) sing=$(grep -a -o 'sing=[0-9]*' sw_$tag.log | sort -u | tr '\n' ' ') $(grep -a 'error CS' sw_$tag.log | head -2)"; }








run d_body body
run d_body_old body -igmNormSweeps 0 -igmOrientHier 0
run d_head head
"$U" -batchmode -nographics -projectPath Headless -runTests -testPlatform EditMode -testResults all.xml -logFile all.log
python - <<'PY'
import re
s=open('Headless/all.xml',encoding='utf-8').read()
print(re.search(r'total="\d+" passed="\d+" failed="\d+" inconclusive="\d+" skipped="\d+"',s).group(0))
for m in re.finditer(r'<test-case (.*?)</test-case>',s,re.S):
    body=m.group(1)
    if 'result="Failed"' in body[:600]:
        name=re.search(r'fullname="([^"]*)"',body).group(1)
        msg=re.search(r'<message><!\[CDATA\[(.*?)\]\]>',body,re.S)
        print('FAIL',name,(msg.group(1).strip()[:300] if msg else ''))
PY
