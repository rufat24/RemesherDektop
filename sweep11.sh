U="/c/Program Files/Unity/Hub/Editor/6000.0.58f2/Editor/Unity.exe"
run(){ tag=$1; asset=$2; shift 2; "$U" -batchmode -nographics -projectPath Headless -executeMethod NativeRemesher.Benchmark.EditorParity.Run -asset $asset -extractor IntegerGrid -targets 4600,5000,5400 -tag _$tag "$@" -quit -logFile sw_$tag.log; echo "$tag $(grep -a -m1 "MEAN" sw_$tag.log) | $(grep -a 'curve network' sw_$tag.log | sort -u | sed 's/;.*//' | tr '\n' ' ') $(grep -a 'error CS' sw_$tag.log | head -2)"; }
run cv_head head -curves
run cv_body body -curves
run cvm8_head head -curves -curveMin 8
run cvm8_body body -curves -curveMin 8
run cvs15_head head -curves -curveSeed 1.5 -curveMin 6
run cvs15_body body -curves -curveSeed 1.5 -curveMin 6
echo SWEEP11_DONE
