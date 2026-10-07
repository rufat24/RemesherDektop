U="/c/Program Files/Unity/Hub/Editor/6000.0.58f2/Editor/Unity.exe"
run(){ tag=$1; shift; "$U" -batchmode -nographics -projectPath Headless -executeMethod NativeRemesher.Benchmark.EditorParity.Run -asset head -extractor IntegerGrid -tag _$tag "$@" -quit -logFile sw_$tag.log; echo "$tag $(grep -a '^  head,IntegerGrid' sw_$tag.log) $(grep -a 'error CS' sw_$tag.log | head -2)"; }
run pq40 -perQuad 40
run a50pq40 -igmGrade -adapt 0.5 -perQuad 40
