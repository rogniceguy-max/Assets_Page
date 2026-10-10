# -*- coding: utf-8 -*-
"""
MuYing Hub - 中文姓名转拼音与系统安全用户名生成
纯 Python 原生字典，零外部依赖
"""
import re

PINYIN_MAP = {
    # 常用姓氏
    '赵':'zhao','钱':'qian','孙':'sun','李':'li','周':'zhou','吴':'wu','郑':'zheng','王':'wang',
    '冯':'feng','陈':'chen','褚':'chu','卫':'wei','蒋':'jiang','沈':'shen','韩':'han','杨':'yang',
    '朱':'zhu','秦':'qin','尤':'you','许':'xu','何':'he','吕':'lv','施':'shi','张':'zhang',
    '孔':'kong','曹':'cao','严':'yan','华':'hua','金':'jin','魏':'wei','陶':'tao','姜':'jiang',
    '戚':'qi','谢':'xie','邹':'zou','喻':'yu','柏':'bai','水':'shui','窦':'dou','章':'zhang',
    '云':'yun','苏':'su','潘':'pan','葛':'ge','奚':'xi','范':'fan','彭':'peng','郎':'lang',
    '鲁':'lu','韦':'wei','昌':'chang','马':'ma','苗':'miao','凤':'feng','花':'hua','方':'fang',
    '俞':'yu','任':'ren','袁':'yuan','柳':'liu','史':'shi','唐':'tang','费':'fei','廉':'lian',
    '岑':'cen','薛':'xue','雷':'lei','贺':'he','倪':'ni','汤':'tang','滕':'teng','殷':'yin',
    '罗':'luo','毕':'bi','郝':'hao','邬':'wu','安':'an','常':'chang','乐':'le','于':'yu',
    '时':'shi','傅':'fu','皮':'pi','卞':'bian','齐':'qi','康':'kang','伍':'wu','余':'yu',
    '元':'yuan','卜':'bo','顾':'gu','孟':'meng','平':'ping','黄':'huang','和':'he','穆':'mu',
    '萧':'xiao','尹':'yin','姚':'yao','邵':'shao','湛':'zhan','汪':'wang','祁':'qi','毛':'mao',
    '禹':'yu','狄':'di','米':'mi','贝':'bei','明':'ming','臧':'zang','计':'ji','伏':'fu',
    '成':'cheng','戴':'dai','谈':'tan','宋':'song','茅':'mao','庞':'pang','熊':'xiong','纪':'ji',
    '舒':'shu','屈':'qu','项':'xiang','祝':'zhu','董':'dong','梁':'liang','杜':'du','阮':'ruan',
    '蓝':'lan','闵':'min','席':'xi','季':'ji','麻':'ma','强':'qiang','贾':'jia','路':'lu',
    '娄':'lou','危':'wei','江':'jiang','童':'tong','颜':'yan','郭':'guo','梅':'mei','盛':'sheng',
    '林':'lin','刁':'diao','钟':'zhong','徐':'xu','邱':'qiu','骆':'luo','高':'gao','夏':'xia',
    '蔡':'cai','田':'tian','樊':'fan','胡':'hu','凌':'ling','霍':'huo','虞':'yu','万':'wan',
    '支':'zhi','柯':'ke','昝':'zan','管':'guan','卢':'lu','莫':'mo','经':'jing','房':'fang',
    '裘':'qiu','缪':'miao','干':'gan','解':'xie','应':'ying','宗':'zong','丁':'ding','宣':'xuan',
    '邓':'deng','郁':'yu','单':'shan','杭':'hang','洪':'hong','包':'bao','诸':'zhu','左':'zuo',
    '石':'shi','崔':'cui','吉':'ji','钮':'niu','龚':'gong','程':'cheng','嵇':'ji','邢':'xing',
    '滑':'hua','裴':'pei','陆':'lu','荣':'rong','翁':'weng','荀':'xun','羊':'yang','於':'yu',
    '惠':'hui','甄':'zhen','曲':'qu','家':'jia','封':'feng','芮':'rui','羿':'yi','储':'chu',
    '靳':'jin','汲':'ji','邴':'bing','糜':'mi','松':'song','井':'jing','段':'duan','富':'fu',
    '巫':'wu','乌':'wu','焦':'jiao','巴':'ba','弓':'gong','牧':'mu','隗':'wei','山':'shan',
    '谷':'gu','车':'che','侯':'hou','宓':'mi','蓬':'peng','全':'quan','郗':'xi','班':'ban',
    '仰':'yang','秋':'qiu','仲':'zhong','伊':'yi','宫':'gong','宁':'ning','仇':'chou','栾':'luan',
    '暴':'bao','甘':'gan','钭':'tou','厉':'li','戎':'rong','祖':'zu','武':'wu','符':'fu',
    '刘':'liu','景':'jing','詹':'zhan','束':'shu','龙':'long','叶':'ye','幸':'xing','司':'si',
    '韶':'shao','郜':'gao','黎':'li','蓟':'ji','薄':'bo','印':'yin','宿':'su','白':'bai',
    '怀':'huai','蒲':'pu','邰':'tai','从':'cong','鄂':'e','索':'suo','咸':'xian','籍':'ji',
    '赖':'lai','卓':'zhuo','蔺':'lin','屠':'tu','蒙':'meng','池':'chi','乔':'qiao','阴':'yin',
    '郁':'yu','胥':'xu','能':'neng','苍':'cang','双':'shuang','闻':'wen','莘':'shen','党':'dang',
    '翟':'zhai','谭':'tan','贡':'gong','劳':'lao','逄':'pang','姬':'ji','申':'shen','扶':'fu',
    '堵':'du','冉':'ran','宰':'zai','郦':'li','雍':'yong','却':'que','璩':'qu','桑':'sang',
    '桂':'gui','濮':'pu','牛':'niu','寿':'shou','通':'tong','边':'bian','扈':'hu','燕':'yan',
    '冀':'ji','郏':'jia','浦':'pu','尚':'shang','农':'nong','温':'wen','别':'bie','庄':'zhuang',
    '晏':'yan','柴':'chai','瞿':'qu','阎':'yan','充':'chong','慕':'mu','连':'lian','茹':'ru',
    '习':'xi','宦':'huan','艾':'ai','鱼':'yu','容':'rong','向':'xiang','古':'gu','易':'yi',
    '慎':'shen','戈':'ge','廖':'liao','庾':'yu','终':'zhong','暨':'ji','居':'ju','衡':'heng',
    '步':'bu','都':'du','耿':'geng','满':'man','弘':'hong','匡':'kuang','国':'guo','文':'wen',
    '寇':'kou','广':'guang','禄':'lu','阙':'que','东':'dong','欧':'ou','殳':'shu','沃':'wo',
    '利':'li','蔚':'wei','越':'yue','夔':'kui','隆':'long','师':'shi','巩':'gong','厍':'she',
    '聂':'nie','晁':'chao','勾':'gou','敖':'ao','融':'rong','冷':'leng','訾':'zi','辛':'xin',
    '阚':'kan','那':'na','简':'jian','饶':'rao','空':'kong','曾':'zeng','毋':'wu','沙':'sha',
    '乜':'nie','养':'yang','鞠':'ju','须':'xu','丰':'feng','巢':'chao','关':'guan','蒯':'kuai',
    '相':'xiang','查':'zha','后':'hou','荆':'jing','红':'hong','游':'you','竺':'zhu','权':'quan',
    '逯':'lu','盖':'gai','益':'yi','桓':'huan','公':'gong','晋':'jin','楚':'chu','闫':'yan',
    # 常用名字汉字
    '一':'yi','二':'er','三':'san','四':'si','五':'wu','六':'liu','七':'qi','八':'ba','九':'jiu','十':'shi',
    '伟':'wei','刚':'gang','勇':'yong','毅':'yi','俊':'jun','峰':'feng','强':'qiang','军':'jun','平':'ping',
    '保':'bao','东':'dong','文':'wen','辉':'hui','力':'li','明':'ming','永':'yong','健':'jian','世':'shi',
    '广':'guang','志':'zhi','义':'yi','兴':'xing','良':'liang','海':'hai','山':'shan','仁':'ren','波':'bo',
    '宁':'ning','贵':'gui','福':'fu','生':'sheng','龙':'long','元':'yuan','全':'quan','国':'guo','胜':'sheng',
    '学':'xue','祥':'xiang','才':'cai','发':'fa','武':'wu','新':'xin','利':'li','清':'qing','飞':'fei',
    '彬':'bin','富':'fu','顺':'shun','信':'xin','子':'zi','杰':'jie','涛':'tao','昌':'chang','成':'cheng',
    '康':'kang','星':'xing','光':'guang','天':'tian','达':'da','安':'an','岩':'yan','中':'zhong','茂':'mao',
    '进':'jin','林':'lin','有':'you','坚':'jian','和':'he','彪':'biao','博':'bo','诚':'cheng','先':'xian',
    '敬':'jing','震':'zhen','振':'zhen','壮':'zhuang','会':'hui','思':'si','群':'qun','豪':'hao','心':'xin',
    '邦':'bang','承':'cheng','乐':'le','绍':'shao','功':'gong','松':'song','善':'shan','厚':'hou','庆':'qing',
    '磊':'lei','民':'min','友':'you','裕':'yu','河':'he','哲':'zhe','江':'jiang','超':'chao','浩':'hao',
    '亮':'liang','政':'zheng','谦':'qian','亨':'heng','奇':'qi','固':'gu','之':'zhi','轮':'lun','翰':'han',
    '朗':'lang','伯':'bo','宏':'hong','言':'yan','若':'ruo','鸣':'ming','朋':'peng','斌':'bin','梁':'liang',
    '栋':'dong','维':'wei','启':'qi','克':'ke','伦':'lun','翔':'xiang','旭':'xu','鹏':'peng','泽':'ze',
    '晨':'chen','辰':'chen','士':'shi','以':'yi','建':'jian','家':'jia','致':'zhi','树':'shu','炎':'yan',
    '德':'de','行':'xing','时':'shi','泰':'tai','盛':'sheng','雄':'xiong','琛':'chen','钧':'jun','冠':'guan',
    '策':'ce','腾':'teng','楠':'nan','榕':'rong','风':'feng','航':'hang','弘':'hong','秀':'xiu','娟':'juan',
    '英':'ying','华':'hua','慧':'hui','巧':'qiao','美':'mei','娜':'na','静':'jing','淑':'shu','惠':'hui',
    '珠':'zhu','翠':'cui','雅':'ya','芝':'zhi','玉':'yu','萍':'ping','红':'hong','娥':'e','玲':'ling',
    '芬':'fen','芳':'fang','燕':'yan','彩':'cai','春':'chun','菊':'ju','兰':'lan','凤':'feng','洁':'jie',
    '梅':'mei','琳':'lin','素':'su','云':'yun','莲':'lian','真':'zhen','环':'huan','雪':'xue','荣':'rong',
    '爱':'ai','妹':'mei','霞':'xia','香':'xiang','月':'yue','莺':'ying','媛':'yuan','艳':'yan','瑞':'rui',
    '凡':'fan','佳':'jia','嘉':'jia','琼':'qiong','勤':'qin','珍':'zhen','贞':'zhen','莉':'li','娣':'di',
    '叶':'ye','璧':'bi','璐':'lu','娅':'ya','琦':'qi','晶':'jing','妍':'yan','茜':'qian','秋':'qiu',
    '珊':'shan','莎':'sha','锦':'jin','黛':'dai','青':'qing','倩':'qian','婷':'ting','姣':'jiao','婉':'wan',
    '娴':'xian','瑾':'jin','颖':'ying','露':'lu','瑶':'yao','怡':'yi','婵':'chan','雁':'yan','蓓':'bei',
    '纨':'wan','仪':'yi','荷':'he','丹':'dan','蓉':'rong','眉':'mei','君':'jun','琴':'qin','蕊':'rui',
    '薇':'wei','菁':'jing','梦':'meng','岚':'lan','苑':'yuan','婕':'jie','馨':'xin','瑗':'yuan','琰':'yan',
    '韵':'yun','融':'rong','园':'yuan','艺':'yi','咏':'yong','卿':'qing','聪':'cong','澜':'lan','纯':'chun',
    '毓':'yu','悦':'yue','昭':'zhao','冰':'bing','爽':'shuang','琬':'wan','茗':'ming','羽':'yu','希':'xi',
    '欣':'xin','飘':'piao','育':'yu','滢':'ying','馥':'fu','筠':'jun','柔':'rou','竹':'zhu','霭':'ai',
    '凝':'ning','晓':'xiao','欢':'huan','霄':'xiao','枫':'feng','芸':'yun','菲':'fei','寒':'han','伊':'yi',
    '亚':'ya','宜':'yi','可':'ke','姬':'ji','舒':'shu','影':'ying','荔':'li','枝':'zhi','思':'si',
    '丽':'li','秀':'xiu','小':'xiao','大':'da','凯':'kai','航':'hang','越':'yue','洋':'yang','宇':'yu',
    '阳':'yang','沐':'mu','涵':'han','梓':'zi','轩':'xuan','博':'bo','睿':'rui','霖':'lin',
    '皓':'hao','辰':'chen','昊':'hao','泽':'ze','语':'yu','彤':'tong','晴':'qing','诺':'nuo','奕':'yi',
    '予':'yu','柯':'ke','源':'yuan','卓':'zhuo','帆':'fan','森':'sen','锋':'feng'
}

def name_to_ascii(fullname: str) -> str:
    """
    将中文或英文全名转换为规范的 Linux/Samba 账户标识
    例如：'张三' -> 'zhangsan'，'Andy Chen' -> 'andychen'
    """
    res = []
    for ch in fullname.strip():
        if '\u4e00' <= ch <= '\u9fff':
            p = PINYIN_MAP.get(ch)
            if p:
                res.append(p)
            else:
                res.append(f"{ord(ch):x}")
        elif ch.isalnum() or ch == '_':
            res.append(ch.lower())
            
    slug = "".join(res)
    if not slug:
        slug = "user"
    if slug[0].isdigit():
        slug = f"u_{slug}"
    return slug[:24]
