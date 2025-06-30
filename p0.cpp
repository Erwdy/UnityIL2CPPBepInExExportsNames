#include "p0.h"
//#include "util/logging.h"
//#define LOG_ERROR LOG
#include <windows.h>
#include <vector>
#include <unordered_map>
#include <cstring>
#include <string>

FILE* logFile = nullptr;

#define LOG(message, ...){fprintf(logFile,"Log  :" message "\n",##__VA_ARGS__);fflush(logFile);}
#define LOG_ERROR(message, ...){fprintf(logFile,"Error:" message "\n",##__VA_ARGS__);fflush(logFile);}


bool FindPeSections(
    HMODULE hModule,
    PIMAGE_NT_HEADERS ntHeaders,
    const std::vector<std::string>& targetSections,
    std::vector<BYTE*>& rStart,
    std::vector<size_t>& rSize
) {
    rStart.clear();
    rSize.clear();
    if (!ntHeaders || targetSections.empty()) return false;
    for (const auto& target : targetSections) {
        bool found = false;
        PIMAGE_SECTION_HEADER section = IMAGE_FIRST_SECTION(ntHeaders);
        for (WORD i = 0; i < ntHeaders->FileHeader.NumberOfSections; ++i) {
            if (target.size() <= 8 &&
                memcmp(section[i].Name, target.c_str(), target.size()) == 0) {
                rStart.push_back((BYTE*)hModule + section[i].VirtualAddress);
                rSize.push_back(section[i].Misc.VirtualSize);
                found = true;
                break;
            }
        }
        if (!found) {
            rStart.push_back(nullptr);
            rSize.push_back(0);
            return true;
        }
    }
    return false;
}

bool writeVectorToFile(const char* filename, const std::vector<std::string>& vec) {
    FILE* file = nullptr;
    errno_t err = fopen_s(&file, filename, "w");
    if (err != 0 || !file) {
        LOG_ERROR("Failed to open file for writing: %s (Error %d)", filename, err);
        return false;
    }
    fprintf(file, "%zd\n", vec.size());
    for (const auto& str : vec) {
        fprintf(file, "%s\n", str.c_str());
    }
    fclose(file);
    return true;
}

bool readVectorFromFile(const char* filename, std::vector<std::string>& vec) {
    FILE* file = nullptr;
    errno_t err = fopen_s(&file, filename, "r");
    if (err != 0 || !file) {
        LOG_ERROR("Failed to open file for reading: %s (Error %d)", filename, err);
        return false;
    }
    vec.clear();

    // 获取文件大小
    fseek(file, 0, SEEK_END);
    long size = ftell(file);
    rewind(file);

    char* buffer = new char[size + 1]; // 分配缓冲区
    size_t bytesRead = fread(buffer, 1, size, file); // 读取文件内容
    buffer[bytesRead] = '\0'; // 添加字符串终止符
    fclose(file);

    char* context = nullptr;
    char* token = strtok_s(buffer, " \n", &context); // 分割第一个token

    if (token != nullptr) {
        int count = atoi(token); // 解析单词数量
        for (int i = 0; i < count; ++i) {
            token = strtok_s(nullptr, " \n", &context); // 继续分割
            if (token == nullptr) break;
            vec.emplace_back(token);
        }
    }

    fclose(file);
    return true;
}


#include <sys/stat.h>
const std::string gameAssembly = "GameAssembly.dll";
size_t getGASize() {
    struct stat statbuf;
    stat(gameAssembly.c_str(), &statbuf);
    size_t filesize = statbuf.st_size;
    return filesize;
}
//#include "capstone.h"

std::unordered_map<std::string, int> str_to_id;
const std::string text = ".text";
const std::string rdata = ".rdata";
const char* sdgah= "savedGAhash.txt";
const char* sdsn="savedSecretName.txt";
const char* sdsnne = "savedSecretNameNoEnc.txt";
std::vector<std::string> id_to_str = {text,rdata };

std::vector<BYTE*> starts;
std::vector<size_t> sizes;

BYTE* rdataStart = nullptr;
size_t rdataSize = 0;
BYTE* textStart = nullptr;
size_t textSize = 0;

std::vector<std::string> secretName;
std::vector<std::string> trueName;
std::unordered_map<std::string, const char*> FakeTrueName;
void InitName() {
    readVectorFromFile(sdsnne, trueName);
    if (trueName.size() != secretName.size()) {
        LOG_ERROR("il2cpp名字长度非预期，%d!=%d", trueName.size(), secretName.size());
    }
    for (int i = 0; i < trueName.size(); i++) {
        FakeTrueName[trueName[i]] = secretName[i].c_str();
    }
    //FakeTrueName["il2cpp_init"] = secretName[0].c_str();
    //FakeTrueName["il2cpp_method_get_name"] = secretName[135].c_str();
    //FakeTrueName["il2cpp_runtime_invoke"] = secretName[166].c_str();
}
bool InitPE(const wchar_t* name, HMODULE* hModule, PIMAGE_DOS_HEADER *dosHeader, PIMAGE_NT_HEADERS *ntHeaders) {
    *hModule = GetModuleHandle(name);
    if (!hModule) {
        LOG_ERROR("获取模块句柄失败。错误代码：%lu", GetLastError());
        return true;
    }
    *dosHeader = reinterpret_cast<PIMAGE_DOS_HEADER>(*hModule);
    if ((*dosHeader)->e_magic != IMAGE_DOS_SIGNATURE) {
        LOG_ERROR("无效的DOS头。");
        return true;
    }
    *ntHeaders = reinterpret_cast<PIMAGE_NT_HEADERS>(
        reinterpret_cast<BYTE*>(*hModule) + (*dosHeader)->e_lfanew);
    if ((*ntHeaders)->Signature != IMAGE_NT_SIGNATURE) {
        LOG_ERROR("无效的NT头。");
        return true;
    }
    return false;
}
#include <inttypes.h>
int PEOprate() {
    HMODULE hModule;
    PIMAGE_DOS_HEADER dosHeader;
    PIMAGE_NT_HEADERS ntHeaders;
    if (InitPE(TEXT("UnityPlayer"), &hModule, &dosHeader, &ntHeaders)) {
        return 1;
    }
    for (int i = 0; i < id_to_str.size(); i++) {
        str_to_id[id_to_str[i]] = i;
    }
    if (FindPeSections(hModule, ntHeaders, id_to_str, starts, sizes)) {
        LOG_ERROR("段寻找错误");
        return 1;
    }
    rdataStart = starts[str_to_id[rdata]];
    rdataSize = sizes[str_to_id[rdata]];
    textStart = starts[str_to_id[text]];
    textSize = sizes[str_to_id[text]];

    //std::reverse(gameAssembly.begin(), gameAssembly.end());
    BYTE* p;
    BYTE* end;

    DWORD_PTR RgameAssembly = 0;
    BYTE* PRgameAssembly = nullptr;
    p = rdataStart;
    end = p + rdataSize - gameAssembly.size() - 1;
    for (; p < end; ++p) {
        if (memcmp(p, gameAssembly.c_str(), gameAssembly.size()) == 0) {
            PRgameAssembly = p;
            RgameAssembly = reinterpret_cast<DWORD_PTR>(p);
            LOG("Found GameAssembly at 0x%llx", RgameAssembly);
            break;
        }
    }
    if (RgameAssembly == 0) {
        LOG_ERROR("GameAssembly未找到");
        return 1;
    }
    FILE* fp = nullptr;
    errno_t err = fopen_s(&fp, "rdata_dump.txt", "wb");
    if (err == 0 && fp != nullptr) {
        fwrite(p - 45, 1, 90 + gameAssembly.size(), fp);
        fclose(fp);
    }
    else {
        LOG_ERROR("文件打开失败，错误码：%d", err);
    }

    p = textStart;
    end = p + textSize;
    DWORD_PTR TrefGA = 0;
    BYTE* xmmwordCache = nullptr;
    for (; p < end - 7; ++p) {//movups  xmm0   this code is only for newer il2cpp (movups  xmm0   not  lea)
        if (p[0] == 0x0f && p[1] == 0x10 && p[2] == 0x05) {
            if (p + 7 + *reinterpret_cast<const int32_t*>(p + 3) - PRgameAssembly == 0) {
                TrefGA = reinterpret_cast<DWORD_PTR>(p);
                xmmwordCache = p;
                p += 7;
                break;
            }
        }
    }
    if (TrefGA == 0) {
        LOG_ERROR("GameAssembly引用未找到");
        return 1;
    }
    BYTE* secretNameFunction = nullptr;
    for (; p < end - 5; ++p) {//call
        if (p[0] == 0xe8) {
            secretNameFunction = p + *reinterpret_cast<const int32_t*>(p + 1) + 5;
            break;
        }
    }
    LOG("函数地址:%" PRIuPTR ", xmmword 和 call 相差地址:%" PRIdPTR, reinterpret_cast<DWORD_PTR>(secretNameFunction),(p-xmmwordCache));

    p = secretNameFunction;
    int tmpCnt = 0;
    for (; p < end - 7; ++p) {//lea
        if (p[0] == 0x48 && p[1] == 0x8D) {
            if (p[2] == 0x15) {
                //if (tmpCnt==0||tmpCnt==135||tmpCnt==166) {
                    secretName.push_back(reinterpret_cast<const char*>(p + 7 + *reinterpret_cast<const int32_t*>(p + 3)));
                //}
                p += 7;
                --p;
                ++tmpCnt;
            }
            else if (p[2]==0x98&&p[3]==0x98&&p[4]==0xcd&&p[5]==0&&p[6]==0) {
                break;
            }
        }
    }
    //if(tmpCnt!=229){
    //    LOG_ERROR("il2cpp名字长度非预期，229!=%d",tmpCnt);
    //    return 1;
    //}
    InitName();
    //for (int i = 0; i < secretName.size();i++) {
    //    LOG("%s",secretName[i].c_str());
    //}
    if (writeVectorToFile(sdsn, secretName)) {
        FILE* fp = nullptr;
        errno_t errTmp0 = fopen_s(&fp, sdgah, "w");
        fprintf(fp,"%zd", getGASize());
        fclose(fp);
        LOG("生成文件成功");
    }
    //csh handle;
    //cs_insn* insn;
    //size_t count;
    //cs_err err = cs_open(CS_ARCH_X86, CS_MODE_64, &handle);
    //if (err != CS_ERR_OK) {
    //    LOG_ERROR("Capstone init failed: %d", err);
    //    return 1;
    //}
    //cs_option(handle, CS_OPT_DETAIL, CS_OPT_ON);
    //count = cs_disasm(handle, code, maxSize, address, 0, &insn);
    //cs_close(&handle);
    return 0;
}

bool isNotInited = true;
bool isFirstGetName = true;
void InitSecretName() {
    //errno_t ign = fopen_s(&logFile, "p0.log", "a+");
    logFile= _fsopen("print0.log", "w", _SH_DENYNO);
    LOG("Begin|开始运行");

    FILE* file = nullptr;
    errno_t err = fopen_s(&file, sdgah, "r");
    if (err != 0 || !file) {
        LOG("大概没有生成缓存");
    }
    else {
        size_t number = 0;
        if (fscanf_s(file, "%zd", &number) == 1) {
            if (getGASize() == number) {
                isNotInited = false;
                readVectorFromFile(sdsn, secretName);
                InitName();
                return;
                //for (int i = 0; i < secretName.size(); i++) {
                //    LOG("%s", secretName[i].c_str());
                //}
            }
        }
        fclose(file);
    }
    PEOprate();
}
const char* GetTrueName(const char* name) {
    if (isNotInited) {
        //try{
        //    HMODULE hModule;
        //    PIMAGE_DOS_HEADER dosHeader;
        //    PIMAGE_NT_HEADERS ntHeaders;
        //    LOG("A")
        //    if (InitPE(TEXT("GameAssembly"), &hModule, &dosHeader, &ntHeaders)) {
        //        LOG_ERROR("读不到GA");
        //    }
        //    else {
        //        LOG("B")
        //        std::vector<BYTE*> starts0;
        //        std::vector<size_t> sizes0;
        //        if (!FindPeSections(hModule, ntHeaders, { "il2cpp" }, starts0, sizes0)) {
        //            //FILE* file = nullptr;
        //            //errno_t err = fopen_s(&file, "il2cppReaded", "r");
        //            LOG("il2cpp段，%zd-%zd", reinterpret_cast<DWORD_PTR>(starts0[0]), sizes0[0]);
        //        }
        //        else {
        //            LOG("error");
        //        }
        //    }
        //}
        //catch (const std::exception& e) {
        //    LOG_ERROR("%s",e.what());
        //}
        InitSecretName();
        isNotInited = false;
    }
    //if (isFirstGetName) {
    //    for (const auto& pair : FakeTrueName) {
    //        LOG("%s(%s)", pair.first.c_str(), pair.second);
    //    }
    //    isFirstGetName = false;
    //}
    std::string sname(name);
    //LOG("%s", FakeTrueName["il2cpp_init"]);
    //for (int i = 0; i < sizeof("il2cpp_init"); i++) {
    //    if (sname[i]== "il2cpp_init"[i]) {
    //        LOG("0");
    //    }
    //    else {
    //        LOG_ERROR("not eq %d %d %d",i, sname[i], "il2cpp_init"[i]);
    //    }
    //}
    if (memcmp("il2cpp", name, 6) == 0) {
        LOG("正在读取：%s(%s)", sname.c_str(), FakeTrueName[sname]);
        return FakeTrueName[sname];
    }
    LOG("非il2cpp函数 %s",name)
    return name;
}
BOOL WINAPI DllMain(HINSTANCE hInstDll, DWORD reasonForDllLoad, LPVOID reserved) {
    if (reasonForDllLoad == DLL_PROCESS_DETACH)
        if(logFile!=nullptr)
            fclose(logFile);
    if (reasonForDllLoad != DLL_PROCESS_ATTACH)
        return TRUE;
    //try {
    //    PEOprate();
    //}
    //catch (const std::exception& e) {
    //    LOG_ERROR("%s",e.what());
    //}
    return TRUE;
}